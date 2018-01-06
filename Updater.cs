using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using Engine.UI;
using Game.External;
using Game.UI;
using LitJson;
using UnityEngine;
using VFS;


namespace Game
{
	public class Updater : MonoBehaviour
	{
		private const int DOWNLOAD_RETRY_TIMES = 5;
		private const float DOWNLOAD_RETRY_DELAY = 0.25f;
		private const int DOWNLOAD_ACCUM_WRITE_INDEX = 256*1024; // 每下载256K字节保存一次索引信息

		private const int STATUS_WORK = 0;
		private const int STATUS_COMPLETE = 1;
		private const int STATUS_RETRY = 2;

		private const int BUTTON_NONE = -1;
		private const int BUTTON_EXIT = 0;
		private const int BUTTON_RETRY = 1;
		private const int BUTTON_UPDATE = 2;
		private const int MESSAGE_CHECKING = 3;
		private const int MESSAGE_PACKAGE_ERROR = 4;
		private const int MESSAGE_NOT_COMPATIBLE = 5;
		private const int MESSAGE_NETWORK_ERROR = 6;
		private const int MESSAGE_NEW_VERSION = 7;
		private const int MESSAGE_PROGRESS = 8;
		private const int MESSAGE_COMPLETE = 9;

		private UpdateNoticeDialog _updateNoticeDialog;
		private UpdateProgressBar _updateProgressBar;
		private LogoDialog _logoDialog;

		private UpdateSetting _updateSetting;
		private UpdateConfig _updateConfig;
		private AssetIndexFile _latest;
		private string _latestVersion;
		private List<AssetInfo> _updateList;
		private int _updateSize = 0;
		private int _downloadedSize = 0;
		private WWW _downloader = null;
		private int _currentAssetSize = 0;
		private int _currentAssetDownloaded = 0;

		private int _updateStatus = STATUS_WORK;
		private bool _skipUpdate = false;
		private bool _confirmDownload = true;


		void Awake()
		{
			_updateProgressBar = UIComponent.FindObjectComponent<UpdateProgressBar>("UpdateProgressBar");
			_updateNoticeDialog = UIComponent.FindObjectComponent<UpdateNoticeDialog>("UpdateNoticeDialog");
			_updateNoticeDialog.Close();
			_logoDialog = UIComponent.FindObjectComponent<LogoDialog>("LogoDialog");

			GameConfig.VFS = true;
			GameConfig.Init();
			GameConfig.InitLogFile();

			ExtSDK.Init();
		}
	
		void Start()
		{
			Debug.Log("Updater.Start");

			// 不开启VFS直接完成
			if (!GameConfig.VFS)
			{
				_updateStatus = STATUS_COMPLETE;
				return;
			}

			StartHotUpdate();
		}

		void Update()
		{
			switch (_updateStatus)
			{
				case STATUS_WORK:
					// 更新当前下载进度条
					if (_downloader != null && _currentAssetSize > 0)
					{
						int assetDownloaded = (int)(_currentAssetSize * _downloader.progress + 0.01f);
						if (assetDownloaded > _currentAssetDownloaded)
						{
							_currentAssetDownloaded = assetDownloaded;
							UpdateProgress();
						}
					}
					break;
				case STATUS_COMPLETE:
					// 更新完毕进入正式场景
					Debug.Log("UpdateCompleted, Application.LoadLevel(1)");
					Application.LoadLevel(1);
					break;
				case STATUS_RETRY:
					// 失败重试
					Debug.Log("UpdateRetry");
					StartHotUpdate();
					break;
			}
		}

		private void OnRetryCallback()
		{
			_updateStatus = STATUS_RETRY;
		}

		// 开始更新
		private void StartHotUpdate()
		{
			// 初始化数据
			_updateSetting = null;
			_updateConfig = null;
			_latest = null;
			_latestVersion = null;

			_updateList = null;
			_updateSize = 0;
			_downloadedSize = 0;
			_downloader = null;

			_updateStatus = STATUS_WORK;

			StartCoroutine(HotUpdate());
		}

		private IEnumerator HotUpdate()
		{
			// 0.读取更新配置信息
			yield return StartCoroutine(LoadUpdateConfig(GameConfig.PERSISTENT_URL + "media/update.json"));
			if (_updateConfig == null)
			{
				yield return StartCoroutine(LoadUpdateConfig(GameConfig.DATA_URL + "media/update.json"));
				if (_updateConfig == null)
				{
					// 读取失败直接关闭游戏
					Debug.LogError("Invalid update config. Application halt!");
					Application.Quit();

					yield break;
				}
			}

			SetProgressInfo(0, GetText(MESSAGE_CHECKING));

			// 1.加载本地文件索引
			yield return StartCoroutine(AssetIndexData.Instance().Load(this));

			GameConfig.LANG = _updateConfig.lang;
			GameConfig.VERSION = AssetIndexData.Instance().GetVersionString();
			UpdateLogo();

			// 是否跳过更新
			if (_skipUpdate)
			{
				_updateStatus = STATUS_COMPLETE;
				yield break;
			}

			// 2.读取最新更新配置文件 
			yield return StartCoroutine(DownloadUpdateSetting());
			if (_updateSetting == null)
			{
				// 是否跳过更新
				if (_skipUpdate)
				{
					_updateStatus = STATUS_COMPLETE;
					yield break;
				}

				ShowNoticeDialog(GetText(MESSAGE_NETWORK_ERROR), BUTTON_RETRY, this.OnRetryCallback);
				yield break;
			}
			
			int verNow = AssetIndexData.Instance().GetVersion();
			int vStatus = _updateSetting.GetStatus(verNow, GameConfig.PLATFORM);
			GameConfig.BETA = (vStatus == VersionStatus.STATUS_BETA);
			GameConfig.REVIEW = (vStatus == VersionStatus.STATUS_REVIEW || vStatus == VersionStatus.STATUS_GOV);
			GameConfig.SERVER_LIST_URL = _updateSetting.svrUrl + "?s=" + vStatus.ToString() + "&p=" + GameConfig.PLATFORM;
			GameConfig.GIFT_CODE_URL = _updateSetting.giftUrl;
			GameConfig.GAME_EVENT_URL = _updateSetting.eventUrl;

			// 通知游戏启动
			EventHelper.DoEvent(EventHelper.EVENT_STARTUP, this);

			Debug.Log("HotUpdate: " + GameConfig.LANG + " " + GameConfig.VERSION + " " + GameConfig.PLATFORM + " BETA=" + GameConfig.BETA + " REVIEW=" + GameConfig.REVIEW);

			// 是否跳过更新
			if (_skipUpdate)
			{
				_updateStatus = STATUS_COMPLETE;
				NotifyUpdateEnd();
				yield break;
			}

			// 3.检查最新版本号
			int verLatest = _updateSetting.GetLastVersion(verNow, GameConfig.PLATFORM);
			if (verLatest == 0)
			{
				// 找不到可更新的版本，跳过更新
				_updateStatus = STATUS_COMPLETE;
				NotifyUpdateEnd();
				yield break;
			}
			_latestVersion = VersionHelper.CodeToString(verLatest);

			// 4.下载最新的文件索引
			yield return StartCoroutine(DownloadLatestIndex());
			if (_latest == null)
			{
				ShowNoticeDialog(GetText(MESSAGE_NETWORK_ERROR), BUTTON_RETRY, this.OnRetryCallback);
				yield break;
			}

			// 5.比较文件数据生成下载列表
			if (!CheckDiff())
			{
				// 出现大版本更新，提示重新安装最新版
				ShowNoticeDialog(GetText(MESSAGE_NOT_COMPATIBLE), BUTTON_UPDATE, this.OnStoreUpdateCallback, false);
				yield break;
			}
			
			// 无需更新, 直接写入最新版本号
			if (_updateList == null || _updateList.Count == 0)
			{
				AssetIndexData.Instance().GetIndex().SetVersion(_latest.GetVersion());
				AssetIndexData.Instance().Save();

				GameConfig.VERSION = AssetIndexData.Instance().GetVersionString();

				SetProgressInfo(0, GetText(MESSAGE_COMPLETE)); // 不设置到100%，不想让玩家看到进度条在加载时又回到0闪烁
				_updateStatus = STATUS_COMPLETE;
				NotifyUpdateEnd();
				yield break;
			}

			// 如果是Wifi环境,无需弹窗确认,直接开始下载
			if (Application.internetReachability == NetworkReachability.ReachableViaLocalAreaNetwork)
				_confirmDownload = false;

			// 如果更新文件小于5M,无需弹窗确认,直接开始下载
			if (_updateSize <= 1024*1024*5)
				_confirmDownload = false;

			// 6.弹出更新确认框, 下载所有更新文件
			if (_confirmDownload)
				ShowNoticeDialog(GetText(MESSAGE_NEW_VERSION, FormatSize(_updateSize)), BUTTON_UPDATE, this.OnDownloadCallback);
			else
				OnDownloadCallback();
		}

		private void NotifyUpdateEnd()
		{
			EventHelper.DoEvent(EventHelper.EVENT_UPDATE_END, this);
		}

		private void UpdateLogo()
		{
			string logo = "logo_zhCN";
			if (!string.IsNullOrEmpty(GameConfig.LANG))
				logo = "logo_" + GameConfig.LANG;

			if (_logoDialog != null) 
				_logoDialog.SetLogo(logo);
		}

		// 出现大版本更新，点击跳转到更新页面
		private void OnStoreUpdateCallback()
		{
			EventHelper.DoEvent(EventHelper.URL_STORE_UPDATE, this);
		}

		private void OnDownloadCallback()
		{
			StartCoroutine(DownloadAssets());
		}

		private void OnExitCallback()
		{
			Application.Quit();
		}

		// 0.加载更新配置文件
		private IEnumerator LoadUpdateConfig(string url)
		{
		//	Debug.Log("Updater.LoadUpdateConfig " + url);

			WWW downloader = new WWW(url);
			yield return downloader;
			
			if (downloader.error != null)
				yield break;

			try 
			{
				_updateConfig = JsonMapper.ToObject<UpdateConfig>(downloader.text);
			}
			catch (Exception)
			{
			//	Debug.Log(e.Message);
				_updateConfig = null;
			}
		}

		// 2.读取最新更新配置文件 
		private IEnumerator DownloadUpdateSetting()
		{
			if (string.IsNullOrEmpty(_updateConfig.url))
				yield break;

			string url = _updateConfig.url
						+ "?spm=" + GetRandomValue()
						+ "&channel_id=" + ExtSDK.GetChannelID()
						+ "&p=" + GameConfig.PLATFORM
						+ "&v=" + GameConfig.VERSION;
		//	Debug.Log("Updater.DownloadUpdateSetting " + url);

			string text = null;
			for (int i = 0; i < DOWNLOAD_RETRY_TIMES; i++)
			{
				WWW downloader = new WWW(url);
				yield return downloader;

				if (downloader.error == null)
				{
					text = downloader.text;
					break;
				}

				yield return new WaitForSeconds(DOWNLOAD_RETRY_DELAY);
		//		Debug.Log("Updater.DownloadUpdateSetting try agin");
			}

			if (string.IsNullOrEmpty(text))
				yield break;

			try
			{
				char[] _LINE_SEPARATORS = { '\r', '\n' };
				string[] lines = text.Split(_LINE_SEPARATORS, StringSplitOptions.RemoveEmptyEntries);

				_updateSetting = new UpdateSetting();
				_updateSetting.resUrl = lines[0];
				_updateSetting.svrUrl = lines[1];
				_updateSetting.giftUrl = lines[2];
				_updateSetting.eventUrl = lines[3];

				for (int i = 4; i < lines.Length; ++i)
				{
					if (string.IsNullOrEmpty(lines[i]))
						continue;

					string[] cols = lines[i].Split(',');

					VersionStatus vs = new VersionStatus();
					vs.version = VersionHelper.StringToCode(cols[0]);
					if (vs.version == 0)
						continue;

					vs.platform = cols[1].Trim();
					vs.status = int.Parse(cols[2]);

					_updateSetting.versions.Add(vs);
				}
			}
			catch (Exception e)
			{
				Debug.Log("DownloadUpdateSetting: " + e.Message);
				_updateSetting = null;
			}
		}

		// 4.下载最新的文件索引
		private IEnumerator DownloadLatestIndex()
		{
			if (GameConfig.PLATFORM == null)
				yield break;

			byte[] bytes = null;
			string url = _updateSetting.resUrl + _latestVersion + '-' + GameConfig.PLATFORM + "/media/file.list?spm=" + GetRandomValue();

		//	Debug.Log("Updater.DownloadLatestIndex " + url);

			for (int i = 0; i < DOWNLOAD_RETRY_TIMES; i++)
			{
				WWW downloader = new WWW(url);
				yield return downloader;

				// 下载成功
				if (downloader.error == null)
				{
					bytes = downloader.bytes;
					break;
				}

				yield return new WaitForSeconds(DOWNLOAD_RETRY_DELAY);
			}
			
			if (bytes == null)
			{
				yield break;
			}
		
			try
			{
				_latest = new AssetIndexFile();
				_latest.Load(new MemoryStream(bytes), AssetInfo.STORAGE_PERSISTEN);
			}
			catch (Exception)
			{
				_latest = null;
			}
		}

		// 5.比较文件数据生成下载列表
		private bool CheckDiff()
		{
		//	Debug.Log("Updater.CheckDiff");

			AssetIndexFile oldAssets = AssetIndexData.Instance().GetIndex();

			// 本地比服务器版本还新, 跳过更新步骤
			if (oldAssets.GetVersion() > _latest.GetVersion())
				return true;

			// 检查是否大版本更新
			if (!VersionHelper.IsCompatible(oldAssets.GetVersion(), _latest.GetVersion()))
				return false;

			// 文件比较
			var allAssets = _latest.FetchAll();
			_updateList = new List<AssetInfo>(allAssets.Count);
			_updateSize = 0;

			foreach (AssetInfo asset in allAssets)
			{
				if (asset == null)
					continue;

				FileInfo fi = new FileInfo(asset.GetWritePath());

				// 检查是否匹配
				AssetInfo old = oldAssets.GetAssetInfo(asset.hash);
				if (old == null || old.IsDiff(asset) || old.IsDiff(fi))
				{
					_updateList.Add(asset);
					_updateSize += asset.size;
				}
			}

			return true;
		}

		// 6.下载所有更新文件
		private IEnumerator DownloadAssets()
		{
		//	Debug.Log("Updater.DownloadAssets " + _updateList.Count);

			Directory.CreateDirectory(GameConfig.PERSISTENT_PATH + "media");

			AssetIndexFile oldAssets = AssetIndexData.Instance().GetIndex();

			int accumSize = 0;
			_downloadedSize = 0;
			UpdateProgress();

			string rootUrl = _updateSetting.resUrl + _latestVersion + '-' + GameConfig.PLATFORM + '/';

			foreach (var asset in _updateList)
			{
				byte[] bytes = null;

				_currentAssetSize = asset.size;
				_currentAssetDownloaded = 0;

				string url = rootUrl + asset.GetFilename() + "?spm=" + GetRandomValue();
				for (int i = 0; i < DOWNLOAD_RETRY_TIMES; i++)
				{
					WWW downloader = new WWW(url);
					_downloader = downloader;
					yield return downloader;

					// 下载成功
					if (downloader.error == null)
					{
						bytes = downloader.bytes;
						break;
					}

					yield return new WaitForSeconds(DOWNLOAD_RETRY_DELAY);
				}

				if (bytes == null || bytes.Length != asset.size)
				{
					if (bytes == null)
						Debug.LogWarning("Updater.DownloadAssets failed " + url);
					else
						Debug.LogWarning("Updater.DownloadAssets invalid size of " + url);

					// 下载更新包失败，弹出重试窗口
					_confirmDownload = false;
					ShowNoticeDialog(GetText(MESSAGE_NETWORK_ERROR), BUTTON_RETRY, OnRetryCallback);
					yield break;
				}

				// 写入文件
				File.WriteAllBytes(asset.GetWritePath(), bytes);

				_currentAssetSize = 0;
				_currentAssetDownloaded = 0;
				_downloader = null;

				_downloadedSize += asset.size;
				accumSize += asset.size;
				UpdateProgress();

				// 替换索引
				oldAssets.AddAssetInfo(asset);

				// 凑齐若干字节后写入索引文件
				if (accumSize >= DOWNLOAD_ACCUM_WRITE_INDEX)
				{
					accumSize = 0;
					AssetIndexData.Instance().Save();
				}
			}

			oldAssets.SetVersion(_latest.GetVersion());
			AssetIndexData.Instance().Save();

			GameConfig.VERSION = AssetIndexData.Instance().GetVersionString();

			SetProgressInfo(1, GetText(MESSAGE_COMPLETE), false);
			_updateStatus = STATUS_COMPLETE;
			NotifyUpdateEnd();
		}


		private const int KB = 1024;
		private const int MB = 1024 * 1024;

		// 格式化文件大小
		private string FormatSize(int size)
		{
			if (size < 1024)
				return "1KB";

			if (size < MB)
				return ((int)(size / KB)).ToString() + "KB";
			
			return ((float)size / MB).ToString("0.0") + "MB";
		}

		// 格式化进度数字
		private string FormatSize(int size1, int size2)
		{
			if (size2 < 1024)
				return (size1 / KB).ToString() + " / 1 KB";

			if (size2 < MB)
				return (size1 / KB).ToString() + " / " + (size2 / KB).ToString() + " KB";

			return ((float)size1 / MB).ToString("0.0") + " / " + ((float)size2 / MB).ToString("0.0") + " MB";
		}
		
		// 根据下载进度更新进度条
		private void UpdateProgress()
		{
			int downloadedSize = _downloadedSize + _currentAssetDownloaded;
			
			float percent = 0;
			if (_updateSize > 0)
				percent = (float)downloadedSize / (float)_updateSize;

			string text = GetText(MESSAGE_PROGRESS, FormatSize(downloadedSize, _updateSize));

			SetProgressInfo(percent, text);
		}

		// 设置进度条信息
		private void SetProgressInfo(float val, string text, bool tween = true)
		{
			_updateProgressBar.SetText(text);
			_updateProgressBar.SetProgress(val, tween);
		}

		// 显示提示框
		private void ShowNoticeDialog(string msg, int btnText, Callback callback, bool autoClose=true)
		{
			_updateNoticeDialog.SetNoticeText(msg);
			_updateNoticeDialog.SetExitText(GetText(BUTTON_EXIT));
			_updateNoticeDialog.SetExitCallback(this.OnExitCallback);

			bool twoBtn = (btnText >= 0);
			if (twoBtn)
			{
				_updateNoticeDialog.SetActText(GetText(btnText));
				_updateNoticeDialog.SetActCallback(callback);
			}

			_updateNoticeDialog.Show(twoBtn, autoClose);
		}

		private string GetText(int id, params object[] args)
		{
			if (_updateConfig == null)
				return string.Empty;

			return _updateConfig.GetText(id, args);
		}

		private static string GetRandomValue()
		{
			return UnityEngine.Random.value.ToString();
		}
	}
}
