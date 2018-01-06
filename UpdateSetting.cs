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
	[System.Reflection.ObfuscationAttribute(Exclude = true)]
	class UpdateConfig
	{
		public string lang = null;
		public string url = null;
		public string[] text = null;

		public string GetText(int id, params object[] args)
		{
			if (text == null || id < 0 || id >= text.Length)
				return string.Empty;

			return string.Format(text[id], args);
		}
	}

	class VersionStatus
	{
		public string platform = null;
		public int version = 0;
		public int status = 0;

		public const int STATUS_BETA = 1;		// 测试版
		public const int STATUS_REVIEW = 2;		// 审核版(iOS/GooglePlay)
		public const int STATUS_ONLINE = 3;		// 正式版
		public const int STATUS_CHANNEL = 998;	// 渠道审核版(Android)
		public const int STATUS_GOV = 999;		// 政府审核版
	}

	class UpdateSetting
	{
		public string resUrl = null;
		public string svrUrl = null;
		public string giftUrl = null;
		public string eventUrl = null;

		public List<VersionStatus> versions = new List<VersionStatus>(256);

		public static bool IsForceBeta()
		{
			// 如果本地有portal.txt，则认为是beta版
			string filePath = GameConfig.PERSISTENT_PATH + "portal.txt";
			return File.Exists(filePath);
		}

		public int GetStatus(int ver, string platform)
		{
			if (IsForceBeta())
				return VersionStatus.STATUS_BETA;

			if (ver < 0)
				return 0;

			if (versions == null || versions.Count == 0)
				return 0;

			foreach (var v in versions)
			{
				if (v.version == ver && v.platform == platform)
					return v.status;
			}

			return 0;
		}

		public int GetLastVersion(int ver, string platform)
		{
			if (ver < 0)
				return 0;

			if (versions == null || versions.Count == 0)
				return 0;

			int status = GetStatus(ver, platform);
			if (status == 0)
				return 0;

			ver = 0;
			foreach (var v in versions)
			{
				if (v.status == status && v.platform == platform)
				{
					if (ver < v.version)
						ver = v.version;
				}
			}

			if (status == VersionStatus.STATUS_BETA && ver == 0)
			{
				// Beta版找不到符合条件的版本，就使用最后一个正式版
				foreach (var v in versions)
				{
					if (v.status == VersionStatus.STATUS_ONLINE && v.platform == platform)
					{
						if (ver < v.version)
							ver = v.version;
					}
				}
			}

			return ver;
		}
	}
}
