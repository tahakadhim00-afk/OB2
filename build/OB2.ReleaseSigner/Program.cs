using System.Security.Cryptography;
using System.Text;
using OB2.Core.Updates;

// D-84: the developer's release key.
//   keygen --out <key.pem>
//       Makes a new ECDSA P-256 key, saved encrypted with a password (PKCS#8, AES-256), and its
//       public half next to it as <key.pem>.pub. The public key goes into
//       src/OB2.Core/Updates/ReleaseKey.cs.
//   sign --key <key.pem> --in <SHA256SUMS.txt> [--out <SHA256SUMS.txt.sig>] [--allow-untrusted]
//       Signs the file, then checks the signature the way OB2 will. A key OB2 does not trust is
//       refused unless --allow-untrusted is given (local test builds only; publish.ps1 never
//       passes it for a GitHub release).
// The password is asked for on the console, or taken from OB2_SIGNING_PASSWORD (scripted tests).

return SignerCommand.Run(args);

internal static class SignerCommand
{
    private const int Usage = 1;
    private const int Failed = 2;
    private const int Untrusted = 3;
    private const int MinPasswordLength = 12;

    public static int Run(string[] args)
    {
        try
        {
            return args.FirstOrDefault() switch
            {
                "keygen" when Option(args, "--out") is { } output => KeyGen(output),
                "sign" when Option(args, "--key") is { } key && Option(args, "--in") is { } input =>
                    Sign(key, input, Option(args, "--out") ?? input + ReleaseSignature.FileSuffix, args.Contains("--allow-untrusted")),
                _ => PrintUsage(),
            };
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException)
        {
            Console.Error.WriteLine(ex.Message);
            return Failed;
        }
    }

    private static int KeyGen(string output)
    {
        output = Path.GetFullPath(output);
        if (File.Exists(output))
        {
            Console.Error.WriteLine($"{output} already exists. Keep it: copies of OB2 that trust it only accept updates signed with it.");
            return Failed;
        }

        var password = ReadPassword("New password for the release key: ");
        if (password.Length < MinPasswordLength)
        {
            Console.Error.WriteLine($"Use at least {MinPasswordLength} characters.");
            return Failed;
        }

        if (Environment.GetEnvironmentVariable("OB2_SIGNING_PASSWORD") is not { Length: > 0 }
            && ReadPassword("The same password again: ") != password)
        {
            Console.Error.WriteLine("The passwords differ. Nothing was saved.");
            return Failed;
        }

        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        var pem = key.ExportEncryptedPkcs8PrivateKeyPem(password, new PbeParameters(PbeEncryptionAlgorithm.Aes256Cbc, HashAlgorithmName.SHA256, 600_000));
        Directory.CreateDirectory(Path.GetDirectoryName(output)!);
        File.WriteAllText(output, pem);
        var publicKey = ReleaseSignature.PublicKeyOf(key);
        File.WriteAllText(output + ".pub", publicKey + Environment.NewLine);

        Console.WriteLine($"Private key: {output}");
        Console.WriteLine($"Public key:  {output}.pub");
        Console.WriteLine();
        Console.WriteLine(publicKey);
        Console.WriteLine();
        Console.WriteLine("Put the public key in src/OB2.Core/Updates/ReleaseKey.cs. Keep a copy of the private key");
        Console.WriteLine("and its password somewhere safe (a USB stick, a password manager): without them no");
        Console.WriteLine("future update can be signed, and every copy of OB2 would have to be updated by hand.");
        return 0;
    }

    private static int Sign(string keyFile, string input, string output, bool allowUntrusted)
    {
        using var key = ECDsa.Create();
        try
        {
            key.ImportFromEncryptedPem(File.ReadAllText(keyFile), ReadPassword("Password for the release key: "));
        }
        catch (Exception ex) when (ex is CryptographicException or ArgumentException)
        {
            Console.Error.WriteLine("Wrong password, or the file is not a release key.");
            return Failed;
        }

        var data = File.ReadAllBytes(input);
        var signature = ReleaseSignature.Sign(data, key);
        if (!ReleaseSignature.Verify(data, signature, ReleaseKey.Trusted))
        {
            var publicKey = ReleaseSignature.PublicKeyOf(key);
            if (!allowUntrusted)
            {
                Console.Error.WriteLine("This key is not one OB2 trusts, so OB2 would refuse the update. Its public key:");
                Console.Error.WriteLine(publicKey);
                Console.Error.WriteLine("Add it to src/OB2.Core/Updates/ReleaseKey.cs, then build and release that version.");
                return Untrusted;
            }

            Console.Error.WriteLine("Warning: signed with a key OB2 does not trust (--allow-untrusted). Do not upload this release.");
        }

        File.WriteAllText(output, signature + "\n");
        Console.WriteLine($"Signed: {output}");
        return 0;
    }

    private static int PrintUsage()
    {
        Console.Error.WriteLine("Usage: keygen --out <key.pem>");
        Console.Error.WriteLine("       sign --key <key.pem> --in <file> [--out <file.sig>] [--allow-untrusted]");
        return Usage;
    }

    private static string? Option(string[] args, string name)
    {
        var index = Array.IndexOf(args, name);
        return index >= 0 && index + 1 < args.Length ? args[index + 1] : null;
    }

    /// <summary>Reads a password without echoing it; prompts go to stderr so stdout stays clean.</summary>
    private static string ReadPassword(string prompt)
    {
        if (Environment.GetEnvironmentVariable("OB2_SIGNING_PASSWORD") is { Length: > 0 } fromEnvironment)
        {
            return fromEnvironment;
        }

        Console.Error.Write(prompt);
        if (Console.IsInputRedirected)
        {
            return Console.ReadLine() ?? string.Empty;
        }

        var text = new StringBuilder();
        while (true)
        {
            var key = Console.ReadKey(intercept: true);
            if (key.Key == ConsoleKey.Enter)
            {
                break;
            }

            if (key.Key == ConsoleKey.Backspace)
            {
                if (text.Length > 0)
                {
                    text.Length--;
                }
            }
            else if (!char.IsControl(key.KeyChar))
            {
                text.Append(key.KeyChar);
            }
        }

        Console.Error.WriteLine();
        return text.ToString();
    }
}
