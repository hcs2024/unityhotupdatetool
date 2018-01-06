using System;
using System.Collections.Generic;
using System.IO;
using DataSheet;
using Engine;


namespace VFS
{
	public class AssetInfo
	{
		public ulong hash;
		public short mode;
		public short storage;
		public uint crc32;
		public int size;
		public string origin;

		public const int STORAGE_STREAMING = 0;
		public const int STORAGE_PERSISTEN = 1;

		private const int MODE_IGNORE = -1;		// 忽略文件
		private const int MODE_RENAME = 0;		// 复制并改名
		private const int MODE_COMPRESS = 1;	// 压缩并改名
		private const int MODE_ORGINAL = 2;		// 原名直接复制
		private const int MODE_DIRECTORY = 3;	// 按目录打包

		public string GetWritePath()
		{
			if (storage != STORAGE_PERSISTEN)
				return null;

			return GameConfig.PERSISTENT_PATH + GetFilename();
		}

		public string GetURL()
		{
			string url = null;
			switch (storage)
			{
				case STORAGE_STREAMING:
					url = GameConfig.DATA_URL;
					break;
				case STORAGE_PERSISTEN:
					url = GameConfig.PERSISTENT_URL;
					break;
				default:
					return null;
			}

			return url + GetFilename();
		}

		public string GetFilename()
		{
			if (mode == MODE_ORGINAL)
				return origin;
			else
				return "media/file-" + hash.ToString("x16");
		}

		public bool IsDiff(AssetInfo val)
		{
			return (hash != val.hash || mode != val.mode || crc32 != val.crc32 || size != val.size);
		}

		public bool IsDiff(FileInfo fi)
		{
			// 如果是包内文件, 忽略比较
			if (storage == STORAGE_STREAMING)
				return false;

			// 文件不存在或者大小不同则认为不同
			if (fi == null || !fi.Exists || fi.Length != size)
				return true;

			return false;
		}
	}


	public class AssetIndexFile
	{
		private int _version = 0;
		private Dictionary<ulong, AssetInfo> _data = new Dictionary<ulong, AssetInfo>(1024);

		public int GetVersion() { return _version; }
		public void SetVersion(int val) { _version = val; }

		public AssetInfo GetAssetInfo(ulong hash)
		{
			AssetInfo val;
			if (!_data.TryGetValue(hash, out val))
				return null;

			return val;
		}

		public AssetInfo GetAssetInfo(string filename)
		{
			ulong hash = HashHelper.Hash64(filename, true);
			return GetAssetInfo(hash);
		}

		public void AddAssetInfo(AssetInfo val)
		{
			_data[val.hash] = val;
		}

		public Dictionary<ulong, AssetInfo>.ValueCollection FetchAll()
		{
			return _data.Values;
		}

		private const int MODE_ORGINAL = 2;		// 原名直接复制

		public void Load(Stream stream, short storage)
		{
			BinaryReader2 reader = new BinaryReader2(stream);

			_version = reader.ReadInt32();

			int count = reader.Read7BitEncodedInt();
			for (int i = 0; i < count; ++i)
			{
				ulong hash = reader.ReadUInt64();

				AssetInfo val;
				if (!_data.TryGetValue(hash, out val))
					val = new AssetInfo();

				val.hash = hash;
				val.mode = reader.ReadInt16();
				val.crc32 = reader.ReadUInt32();
				val.size = reader.Read7BitEncodedInt();
				val.storage = storage;

				if (val.mode == MODE_ORGINAL)
					val.origin = reader.ReadString();

				_data[hash] = val;
			}
		}

		public void Save(Stream stream)
		{
			BinaryWriter2 bw = new BinaryWriter2(stream);

			bw.Write(_version);
			bw.Write7BitEncodedInt(_data.Count);

			foreach (var file in _data.Values)
			{
				bw.Write(file.hash);
				bw.Write(file.mode);
				bw.Write(file.crc32);
				bw.Write7BitEncodedInt(file.size);

				if (file.mode == MODE_ORGINAL)
					bw.Write(file.origin);
			}

			bw.Flush();
		}

		public void Clear()
		{
			_version = 0;
			_data.Clear();
		}
	}
}
