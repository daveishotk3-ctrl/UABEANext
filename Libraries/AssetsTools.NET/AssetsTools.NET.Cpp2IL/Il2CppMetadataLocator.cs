using System;
using System.IO;

namespace AssetsTools.NET.Cpp2IL
{
    /// <summary>
    /// Finds IL2CPP metadata that is not a standalone global-metadata.dat file, for example
    /// metadata that has been embedded inside the native IL2CPP binary (GameAssembly.dll,
    /// libil2cpp.so, data.wasm...), or a file that has the metadata somewhere after the start.
    /// </summary>
    public static class Il2CppMetadataLocator
    {
        public const uint MetadataMagic = 0xFAB11BAF;
        public const int MinSupportedVersion = 23;
        public const int MaxSupportedVersion = 108;

        // a real header is followed by section headers (offset, size) and the first
        // section starts right after the header, so its offset is the header size.
        private const int HeaderPrefixSize = 16;
        private const int MinFirstSectionOffset = 0x40;
        private const int MaxFirstSectionOffset = 0x4000;

        /// <summary>
        /// Checks if a plausible IL2CPP metadata header starts at the given offset.
        /// </summary>
        public static bool HasMetadataHeaderAt(byte[] data, long offset)
        {
            if (data == null || offset < 0 || offset + HeaderPrefixSize > data.Length)
                return false;

            int pos = (int)offset;
            if (BitConverter.ToUInt32(data, pos) != MetadataMagic)
                return false;

            int version = BitConverter.ToInt32(data, pos + 4);
            if (version < MinSupportedVersion || version > MaxSupportedVersion)
                return false;

            int firstSectionOffset = BitConverter.ToInt32(data, pos + 8);
            int firstSectionSize = BitConverter.ToInt32(data, pos + 12);
            if (firstSectionOffset < MinFirstSectionOffset || firstSectionOffset > MaxFirstSectionOffset)
                return false;

            long remaining = data.Length - offset;
            if (firstSectionSize < 0 || firstSectionOffset + (long)firstSectionSize > remaining)
                return false;

            return true;
        }

        /// <summary>
        /// Searches a whole file for the start of IL2CPP metadata.
        /// </summary>
        /// <returns>The offset of the metadata header, or -1 if none was found.</returns>
        public static int FindMetadataOffset(byte[] data)
        {
            if (data == null || data.Length < HeaderPrefixSize)
                return -1;

            // the magic is stored little-endian, so the first byte in the file is its lowest byte
            byte firstMagicByte = (byte)(MetadataMagic & 0xFF);
            int lastStart = data.Length - HeaderPrefixSize;

            int pos = 0;
            while (pos <= lastStart)
            {
                int index = Array.IndexOf(data, firstMagicByte, pos);
                if (index < 0 || index > lastStart)
                    return -1;

                if (HasMetadataHeaderAt(data, index))
                    return index;

                pos = index + 1;
            }

            return -1;
        }

        /// <summary>
        /// Returns bytes that start at an IL2CPP metadata header. If the metadata bytes already start with
        /// one they are returned as-is. Otherwise the metadata bytes, then the assembly bytes, are searched
        /// for embedded metadata. If nothing is found, the metadata bytes are returned unchanged so the
        /// normal parser can report its own error.
        /// </summary>
        public static byte[] ExtractMetadata(byte[] metadataBytes, byte[] assemblyBytes)
        {
            if (metadataBytes != null && metadataBytes.Length >= 4 && BitConverter.ToUInt32(metadataBytes, 0) == MetadataMagic)
                return metadataBytes;

            int offset = FindMetadataOffset(metadataBytes);
            if (offset >= 0)
                return SliceFrom(metadataBytes, offset);

            if (assemblyBytes != null && !ReferenceEquals(assemblyBytes, metadataBytes))
            {
                offset = FindMetadataOffset(assemblyBytes);
                if (offset >= 0)
                    return SliceFrom(assemblyBytes, offset);
            }

            return metadataBytes;
        }

        /// <summary>
        /// Checks if a file has IL2CPP metadata embedded in it. Reads the whole file.
        /// </summary>
        public static bool FileContainsMetadata(string path)
        {
            try
            {
                return FindMetadataOffset(File.ReadAllBytes(path)) >= 0;
            }
            catch (IOException)
            {
                return false;
            }
            catch (UnauthorizedAccessException)
            {
                return false;
            }
        }

        private static byte[] SliceFrom(byte[] data, int offset)
        {
            if (offset == 0)
                return data;

            byte[] result = new byte[data.Length - offset];
            Buffer.BlockCopy(data, offset, result, 0, result.Length);
            return result;
        }
    }
}
