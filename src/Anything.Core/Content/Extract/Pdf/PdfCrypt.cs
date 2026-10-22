using System.Security.Cryptography;

namespace Anything.Core.Content.Extract;

/// <summary>
/// The standard security handler with an empty user password: RC4 40/128 (R2/R3), RC4 or AES-128 crypt filters (R4),
/// AES-256 (R5/R6). Strings and streams of objects read from the file are decrypted with that object's key; objects
/// inside object streams are not (their stream was); cross-reference streams never are, nor the metadata stream when
/// /EncryptMetadata is false.
/// </summary>
internal sealed class PdfCrypt
{
    /// <summary>How the document's security handler turned out.</summary>
    public enum Outcome : byte { Open, NeedsPassword, Unsupported }

    enum Method : byte { None, Rc4, AesV2, AesV3 }

    static readonly byte[] Pad =
    [
        0x28, 0xBF, 0x4E, 0x5E, 0x4E, 0x75, 0x8A, 0x41, 0x64, 0x00, 0x4E, 0x56, 0xFF, 0xFA, 0x01, 0x08,
        0x2E, 0x2E, 0x00, 0xB6, 0xD0, 0x68, 0x3E, 0x80, 0x2F, 0x0C, 0xA9, 0xFE, 0x64, 0x53, 0x69, 0x7A,
    ];

    readonly byte[] _key;
    readonly Method _strings, _streams;
    readonly int _revision;

    PdfCrypt(byte[] key, Method strings, Method streams, int revision, bool encryptMetadata)
    {
        _key = key;
        _strings = strings;
        _streams = streams;
        _revision = revision;
        EncryptMetadata = encryptMetadata;
    }

    /// <summary>The XMP metadata stream is encrypted too (/EncryptMetadata, default true).</summary>
    public bool EncryptMetadata { get; }

    /// <summary>Opens the handler of an /Encrypt dictionary with the empty user password.</summary>
    public static Outcome Open(PdfDict encrypt, byte[]? firstId, out PdfCrypt? crypt)
    {
        crypt = null;
        if (encrypt.Name("Filter") != "Standard") return Outcome.Unsupported;
        int v = Int(encrypt["V"], 0);
        int r = Int(encrypt["R"], 0);
        bool encryptMetadata = encrypt["EncryptMetadata"] is not PdfBool { Value: false };
        byte[] o = Bytes(encrypt["O"]);
        byte[] u = Bytes(encrypt["U"]);
        int p = unchecked((int)(long)Num(encrypt["P"], 0));
        firstId ??= [];
        try
        {
            switch (r)
            {
                case 2:
                case 3:
                case 4:
                    {
                        if (o.Length < 32 || u.Length < 32) return Outcome.Unsupported;
                        int lengthBits = v == 1 || r == 2 ? 40 : Int(encrypt["Length"], 40);
                        Method strings = Method.Rc4, streams = Method.Rc4;
                        if (v == 4)
                        {
                            lengthBits = 128;
                            if (!CryptFilters(encrypt, out strings, out streams, ref lengthBits)) return Outcome.Unsupported;
                        }
                        else if (v is not (1 or 2)) return Outcome.Unsupported;
                        if (lengthBits % 8 != 0 || lengthBits < 40 || lengthBits > 128) lengthBits = 128;
                        int n = r == 2 ? 5 : lengthBits / 8;
                        byte[] key = KeyR234(o, p, firstId, r, n, encryptMetadata);
                        if (!CheckUserR234(key, u, firstId, r)) return Outcome.NeedsPassword;
                        crypt = new PdfCrypt(key, strings, streams, r, encryptMetadata);
                        return Outcome.Open;
                    }
                case 5:
                case 6:
                    {
                        byte[] ue = Bytes(encrypt["UE"]);
                        if (u.Length < 48 || ue.Length < 32) return Outcome.Unsupported;
                        var validation = u.AsSpan(32, 8);
                        var keySalt = u.AsSpan(40, 8);
                        byte[] check = r == 5 ? SHA256.HashData(validation) : Hash2B([], validation, []);
                        if (!check.AsSpan(0, 32).SequenceEqual(u.AsSpan(0, 32))) return Outcome.NeedsPassword;
                        byte[] intermediate = r == 5 ? SHA256.HashData(keySalt) : Hash2B([], keySalt, []);
                        using var aes = Aes.Create();
                        aes.Key = intermediate[..32];
                        byte[] fileKey = aes.DecryptCbc(ue.AsSpan(0, 32), new byte[16], PaddingMode.None);
                        Method strings = Method.AesV3, streams = Method.AesV3;
                        int bits = 256;
                        if (v >= 4 && encrypt["CF"] is PdfDict && !CryptFilters(encrypt, out strings, out streams, ref bits)) return Outcome.Unsupported;
                        crypt = new PdfCrypt(fileKey, strings, streams, r, encryptMetadata);
                        return Outcome.Open;
                    }
                default:
                    return Outcome.Unsupported;
            }
        }
        catch (CryptographicException) { return Outcome.Unsupported; }
    }

    /// <summary>The /StrF and /StmF crypt filters of a V4/V5 dictionary.</summary>
    static bool CryptFilters(PdfDict encrypt, out Method strings, out Method streams, ref int lengthBits)
    {
        var cf = encrypt["CF"] as PdfDict;
        strings = FilterMethod(cf, encrypt.Name("StrF") ?? "Identity", ref lengthBits);
        streams = FilterMethod(cf, encrypt.Name("StmF") ?? "Identity", ref lengthBits);
        return strings != (Method)255 && streams != (Method)255;
    }

    static Method FilterMethod(PdfDict? cf, string name, ref int lengthBits)
    {
        if (name == "Identity") return Method.None;
        if (cf?[name] is not PdfDict filter) return (Method)255;
        switch (filter.Name("CFM"))
        {
            case "V2":
                // /Length in a crypt filter is in bytes (some writers put bits).
                if (filter["Length"] is PdfNumber l) lengthBits = l.Value <= 16 ? (int)l.Value * 8 : (int)l.Value;
                return Method.Rc4;
            case "AESV2": return Method.AesV2;
            case "AESV3": return Method.AesV3;
            case "None" or null: return Method.None;
            default: return (Method)255;
        }
    }

    /// <summary>Algorithm 2: the file key from the empty password.</summary>
    static byte[] KeyR234(byte[] o, int p, byte[] firstId, int r, int n, bool encryptMetadata)
    {
        var input = new List<byte>(32 + 32 + 4 + firstId.Length + 4);
        input.AddRange(Pad);
        input.AddRange(o.AsSpan(0, 32).ToArray());
        input.AddRange(BitConverter.GetBytes(p));
        input.AddRange(firstId);
        if (r >= 4 && !encryptMetadata) input.AddRange([0xFF, 0xFF, 0xFF, 0xFF]);
        byte[] hash = MD5.HashData(input.ToArray());
        if (r >= 3)
            for (int i = 0; i < 50; i++) hash = MD5.HashData(hash.AsSpan(0, n));
        return hash[..n];
    }

    /// <summary>Algorithms 4 and 5: the key opens the document when /U comes out of it.</summary>
    static bool CheckUserR234(byte[] key, byte[] u, byte[] firstId, int r)
    {
        if (r == 2) return Rc4(key, Pad).AsSpan().SequenceEqual(u.AsSpan(0, 32));
        var input = new byte[32 + firstId.Length];
        Pad.CopyTo(input, 0);
        firstId.CopyTo(input, 32);
        byte[] x = Rc4(key, MD5.HashData(input));
        var k = new byte[key.Length];
        for (int i = 1; i <= 19; i++)
        {
            for (int j = 0; j < key.Length; j++) k[j] = (byte)(key[j] ^ i);
            x = Rc4(k, x);
        }
        return x.AsSpan(0, 16).SequenceEqual(u.AsSpan(0, 16));
    }

    /// <summary>Algorithm 2.B (R6): the hash of a password with a salt and optional user data.</summary>
    internal static byte[] Hash2B(ReadOnlySpan<byte> password, ReadOnlySpan<byte> salt, ReadOnlySpan<byte> userData)
    {
        var first = new byte[password.Length + salt.Length + userData.Length];
        password.CopyTo(first);
        salt.CopyTo(first.AsSpan(password.Length));
        userData.CopyTo(first.AsSpan(password.Length + salt.Length));
        byte[] k = SHA256.HashData(first);
        using var aes = Aes.Create();
        for (int round = 0; ; round++)
        {
            int unit = password.Length + k.Length + userData.Length;
            var k1 = new byte[unit * 64];
            for (int i = 0; i < 64; i++)
            {
                var at = k1.AsSpan(i * unit);
                password.CopyTo(at);
                k.CopyTo(at[password.Length..]);
                userData.CopyTo(at[(password.Length + k.Length)..]);
            }
            aes.Key = k[..16];
            byte[] e = aes.EncryptCbc(k1, k.AsSpan(16, 16), PaddingMode.None);
            int sum = 0;
            for (int i = 0; i < 16; i++) sum += e[i];
            k = (sum % 3) switch { 0 => SHA256.HashData(e), 1 => SHA384.HashData(e), _ => SHA512.HashData(e) };
            if (round >= 63 && e[^1] <= round - 31) break;
        }
        return k[..32];
    }

    /// <summary>Decrypts a string of an object read from the file.</summary>
    public byte[] DecryptString(byte[] data, int number, int generation) => Decrypt(data, number, generation, _strings);

    /// <summary>Decrypts a stream's data into a pooled buffer (not called for cross-reference streams or unencrypted metadata).</summary>
    public PdfBytes DecryptStream(ReadOnlySpan<byte> data, int number, int generation)
    {
        switch (_streams)
        {
            case Method.Rc4:
                {
                    var output = PdfPool.Rent(data.Length);
                    Rc4(ObjectKey(number, generation, aes: false), data, output);
                    return new PdfBytes(output, data.Length, false, Pooled: true);
                }
            case Method.AesV2: return AesCbcPooled(ObjectKey(number, generation, aes: true), data);
            case Method.AesV3: return AesCbcPooled(_key, data);
            default:
                {
                    var copy = PdfPool.Rent(data.Length);
                    data.CopyTo(copy);
                    return new PdfBytes(copy, data.Length, false, Pooled: true);
                }
        }
    }

    /// <summary>AES-CBC into a pooled buffer, as <see cref="AesCbc"/>: the IV first, a bad last block or padding keeps what decrypted.</summary>
    static PdfBytes AesCbcPooled(byte[] key, ReadOnlySpan<byte> data)
    {
        int body = (data.Length - 16) & ~15;
        if (body <= 0) return PdfBytes.Empty;
        var plain = PdfPool.Rent(body);
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            int n = aes.DecryptCbc(data.Slice(16, body), data[..16], plain, PaddingMode.None);
            int pad = n > 0 ? plain[n - 1] : 0;
            if (pad is >= 1 and <= 16 && pad <= n)
            {
                bool ok = true;
                for (int i = n - pad; i < n; i++) ok &= plain[i] == pad;
                if (ok) n -= pad;
            }
            return new PdfBytes(plain, n, false, Pooled: true);
        }
        catch (CryptographicException)
        {
            PdfPool.Return(plain);
            return PdfBytes.Empty;
        }
    }

    byte[] Decrypt(byte[] data, int number, int generation, Method method)
    {
        switch (method)
        {
            case Method.None: return data;
            case Method.Rc4: return Rc4(ObjectKey(number, generation, aes: false), data);
            case Method.AesV2: return AesCbc(ObjectKey(number, generation, aes: true), data);
            case Method.AesV3: return AesCbc(_key, data);
            default: return data;
        }
    }

    /// <summary>Algorithm 1: the file key extended with the object number and generation (and "sAlT" for AES).</summary>
    byte[] ObjectKey(int number, int generation, bool aes)
    {
        if (_revision >= 5) return _key;
        var input = new byte[_key.Length + 5 + (aes ? 4 : 0)];
        _key.CopyTo(input, 0);
        int at = _key.Length;
        input[at] = (byte)number; input[at + 1] = (byte)(number >> 8); input[at + 2] = (byte)(number >> 16);
        input[at + 3] = (byte)generation; input[at + 4] = (byte)(generation >> 8);
        if (aes) { input[at + 5] = 0x73; input[at + 6] = 0x41; input[at + 7] = 0x6C; input[at + 8] = 0x54; }
        byte[] hash = MD5.HashData(input);
        return hash[..Math.Min(_key.Length + 5, 16)];
    }

    /// <summary>AES-CBC with the IV in the first 16 bytes; a bad last block or padding keeps what decrypted.</summary>
    static byte[] AesCbc(byte[] key, byte[] data)
    {
        int body = (data.Length - 16) & ~15;
        if (body <= 0) return [];
        try
        {
            using var aes = Aes.Create();
            aes.Key = key;
            byte[] plain = aes.DecryptCbc(data.AsSpan(16, body), data.AsSpan(0, 16), PaddingMode.None);
            int pad = plain[^1];
            if (pad is >= 1 and <= 16 && pad <= plain.Length)
            {
                bool ok = true;
                for (int i = plain.Length - pad; i < plain.Length; i++) ok &= plain[i] == pad;
                if (ok) return plain[..^pad];
            }
            return plain;
        }
        catch (CryptographicException) { return []; }
    }

    /// <summary>RC4 (ARC4).</summary>
    internal static byte[] Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data)
    {
        var output = new byte[data.Length];
        Rc4(key, data, output);
        return output;
    }

    /// <summary>RC4 (ARC4) into output (at least data's length).</summary>
    static void Rc4(ReadOnlySpan<byte> key, ReadOnlySpan<byte> data, Span<byte> output)
    {
        if (key.IsEmpty) { data.CopyTo(output); return; }
        Span<byte> s = stackalloc byte[256];
        for (int i = 0; i < 256; i++) s[i] = (byte)i;
        for (int i = 0, j = 0; i < 256; i++)
        {
            j = (j + s[i] + key[i % key.Length]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
        }
        for (int k = 0, i = 0, j = 0; k < data.Length; k++)
        {
            i = (i + 1) & 0xFF;
            j = (j + s[i]) & 0xFF;
            (s[i], s[j]) = (s[j], s[i]);
            output[k] = (byte)(data[k] ^ s[(s[i] + s[j]) & 0xFF]);
        }
    }

    static int Int(PdfObject? o, int fallback) => o is PdfNumber n && double.IsFinite(n.Value) ? (int)Math.Clamp(n.Value, int.MinValue, int.MaxValue) : fallback;
    static double Num(PdfObject? o, double fallback) => o is PdfNumber n ? n.Value : fallback;
    static byte[] Bytes(PdfObject? o) => o is PdfString s ? s.Bytes : [];
}
