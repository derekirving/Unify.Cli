namespace Unify.Cli.Extensions
{
    public static class Extensions
    {
        public static string ToBase16(this byte[] binary, Base16Config config = null)
        {
            if (config == null)
                config = Base16Config.HexUppercase;
            var base16table = config.Base16table;

            var chars = new char[binary.Length * 2];
            for (int i = 0, b; i < binary.Length; ++i)
            {
                b = binary[i];
                chars[i * 2] = base16table[b >> 4];
                chars[i * 2 + 1] = base16table[b & 0xF];
            }
            return new string(chars);
        }
	}
}
