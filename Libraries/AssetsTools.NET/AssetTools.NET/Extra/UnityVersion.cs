using System;

namespace AssetsTools.NET.Extra
{
    public class UnityVersion
    {
        public int major;
        public int minor;
        public int patch;
        public string type;
        public int typeNum;

        public UnityVersion() { }

        public UnityVersion(string version)
        {
            major = 0;
            minor = 0;
            patch = 0;
            type = "";
            typeNum = 0;

            string[] versionSplit = version.Split('.');

            if (versionSplit.Length >= 1)
                major = int.Parse(versionSplit[0]);
            if (versionSplit.Length >= 2)
                minor = int.Parse(versionSplit[1]);
            if (versionSplit.Length <= 2)
                return;

            int verTypeIndex = versionSplit[2].IndexOfAny(new[] { 'f', 'p', 'a', 'b', 'c', 't', 'x' });
            if (verTypeIndex != -1)
            {
                type = versionSplit[2][verTypeIndex].ToString();
                patch = int.Parse(versionSplit[2].Substring(0, verTypeIndex));

                string patchNumString = versionSplit[2].Substring(verTypeIndex + 1);
                if (!int.TryParse(patchNumString, out typeNum))
                {
                    // sometimes pesky custom engine versions add text to the end
                    string newPatchNumString = "";
                    for (int i = 0; i < patchNumString.Length; i++)
                    {
                        if (patchNumString[i] >= '0' && patchNumString[i] <= '9')
                        {
                            newPatchNumString += patchNumString[i];
                        }
                        else
                        {
                            break;
                        }
                    }
                    if (newPatchNumString.Length > 0)
                    {
                        typeNum = int.Parse(newPatchNumString);
                    }
                }
            }
            else
            {
                patch = int.Parse(versionSplit[2]);
                type = "";
                typeNum = 0;
            }
        }

        /// <summary>
        /// Try to parse a Unity version string such as "2022.3.10f1" or "6000.0.23f1".
        /// Returns false instead of throwing for strings that aren't real versions (e.g. "5.x.x" or "").
        /// </summary>
        public static bool TryParse(string version, out UnityVersion result)
        {
            result = null;
            if (string.IsNullOrEmpty(version))
                return false;

            try
            {
                result = new UnityVersion(version);
                return true;
            }
            catch (FormatException)
            {
                result = null;
                return false;
            }
            catch (OverflowException)
            {
                result = null;
                return false;
            }
        }

        /// <summary>
        /// Checks if this version is the same as or newer than the given major.minor.patch.
        /// </summary>
        public bool IsAtLeast(int major, int minor = 0, int patch = 0)
        {
            if (this.major != major)
                return this.major > major;
            if (this.minor != minor)
                return this.minor > minor;
            return this.patch >= patch;
        }

        /// <summary>
        /// True for Unity 6 (6000.x) and anything newer.
        /// </summary>
        public bool IsUnity6OrNewer => major >= 6000;

        /// <summary>
        /// A friendly name for the version. Unity 6 and newer use the major version divided by 1000
        /// (6000.3.2f1 is "Unity 6.3"). Older versions are returned as "2022.3".
        /// </summary>
        public string ToMarketingString()
        {
            if (major >= 6000 && major % 1000 == 0)
                return $"Unity {major / 1000}.{minor}";

            return $"{major}.{minor}";
        }

        public override string ToString()
        {
            if (type == string.Empty)
                return $"{major}.{minor}.{patch}";
            else
                return $"{major}.{minor}.{patch}{type}{typeNum}";
        }

        public ulong ToUInt64()
        {
            byte typeByte = type switch
            {
                "a" => 0,
                "b" => 1,
                "c" => 2,
                "f" => 3,
                "p" => 4,
                "x" => 5,
                _ => 0xff
            };

            return ((ulong)major << 48) | ((ulong)minor << 32) | ((ulong)patch << 16) | ((ulong)typeByte << 8) | (uint)typeNum;
        }

        public static UnityVersion FromUInt64(ulong data)
        {
            UnityVersion version = new UnityVersion()
            {
                major = (int)((data >> 48) & 0xffff),
                minor = (int)((data >> 32) & 0xffff),
                patch = (int)((data >> 16) & 0xffff),
                type = ((data >> 8) & 0xff) switch
                {
                    0 => "a",
                    1 => "b",
                    2 => "c",
                    3 => "f",
                    4 => "p",
                    5 => "x",
                    _ => "?"
                },
                typeNum = (int)(data & 0xff)
            };

            return version;
        }
    }
}
