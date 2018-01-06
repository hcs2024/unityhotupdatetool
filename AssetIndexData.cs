using System;
using System.Collections;
using System.IO;
using Module;
using UnityEngine;


namespace VFS
{
	public class AssetIndexData
	{
		private static AssetIndexData _instance = new AssetIndexData();
		public static AssetIndexData Instance() { return _instance; }

		private AssetIndexFile _index = new AssetIndexFile();
		
		public AssetIndexFile GetIndex()
		{
			return _index;
		}

		public int GetVersion()
		{
			return _index.GetVersion();
		}

		public string GetVersionString()
		{
			return VersionHelper.CodeToString(_index.GetVersion());
		}

		public IEnumerator Load(MonoBehaviour mb)
		{
			_index.Clear();

			AssetIndexFile indexP = new AssetIndexFile();

			yield return mb.StartCoroutine(LoadImpl(GameConfig.DATA_URL + "media/file.list", AssetInfo.STORAGE_STREAMING, _index));
			yield return mb.StartCoroutine(LoadImpl(GameConfig.PERSISTENT_URL + "media/file.list", AssetInfo.STORAGE_PERSISTEN, indexP));

			// 流数据文件版本更高,例如直接安装了app新版本
			if (indexP.GetVersion() == 0 || _index.GetVersion() > indexP.GetVersion())
				yield break;

			// 逐个比较文件索引,判定使用哪个文件
			_index.SetVersion(indexP.GetVersion());
			foreach (var asset in indexP.FetchAll())
			{
				if (asset == null)
					continue;

				// 文件不存在或有差异则用PERSISTEN的条目覆盖
				AssetInfo old = _index.GetAssetInfo(asset.hash);
				if (old == null || old.IsDiff(asset)) 
					_index.AddAssetInfo(asset);
			}
		}

		private IEnumerator LoadImpl(string url, short storage, AssetIndexFile index)
		{
			index.Clear();

			WWW downloader = new WWW(url);
			yield return downloader;

			if (downloader.error != null)
				yield break;

			try
			{
				index.Load(new MemoryStream(downloader.bytes), storage);
			}
			catch (Exception e) 
			{
				index.Clear();
				Log.Warning("AssetIndexData.LoadImpl(" + url + ") " + e.Message);
			}
		}


		public void Save()
		{
			Directory.CreateDirectory(GameConfig.PERSISTENT_PATH + "media");
			SaveImpl(GameConfig.PERSISTENT_PATH + "media/file.list");
		}

		private void SaveImpl(string path)
		{
			try
			{
				FileStream fs = new FileStream(path, FileMode.Create);
				_index.Save(fs);
				fs.Close();
			}
			catch (Exception e)
			{
				Log.Error("AssetIndexData.SaveImpl(" + path + ") " + e);
			}
		}
	}
}
