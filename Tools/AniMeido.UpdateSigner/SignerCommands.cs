using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using AniMeido.Updates;

namespace AniMeido.UpdateSigner;

internal static class SignerCommands
{
    internal static async Task<int> RunAsync(string[] args, TextWriter output, TextWriter error)
    {
        try
        {
            var options = Parse(args);
            switch (args[0])
            {
                case "keygen":
                    Keygen(options);
                    output.WriteLine("密钥文件已生成；请离线保存并备份私钥。");
                    break;
                case "manifest":
                    Manifest(options);
                    output.WriteLine("更新清单已生成并通过字段校验。");
                    break;
                case "sign":
                    Sign(options);
                    output.WriteLine("更新清单签名已生成。");
                    break;
                case "verify":
                    var manifest = await VerifyAsync(options).ConfigureAwait(false);
                    output.WriteLine($"版本：{manifest.Version}；通道：{manifest.Channel}");
                    break;
            }

            return 0;
        }
        catch (UsageException ex)
        {
            error.WriteLine($"参数错误：{ex.Message}");
            return 2;
        }
        catch (UpdateException ex)
        {
            error.WriteLine(ex.Message);
            return 1;
        }
        catch (Exception ex) when (ex is IOException or UnauthorizedAccessException
            or CryptographicException or FormatException or ArgumentException)
        {
            // 仅显示错误类别；底层异常可能带私钥输入，不将其内容写入 stderr。
            error.WriteLine($"操作失败（{ex.GetType().Name}）：请检查文件、密钥和权限。");
            return 1;
        }
    }

    private static Dictionary<string, List<string>> Parse(string[] args)
    {
        if (args.Length == 0)
        {
            throw new UsageException("需要子命令：keygen、manifest、sign 或 verify。");
        }

        var allowed = args[0] switch
        {
            "keygen" => new[] { "--private", "--public" },
            "manifest" => new[] { "--zip", "--version", "--channel", "--url", "--release-date",
                "--min-updatable-from", "--notes-file", "--out" },
            "sign" => new[] { "--private", "--file", "--overwrite" },
            "verify" => new[] { "--public", "--file", "--channel" },
            _ => throw new UsageException("未知子命令。")
        };
        var options = new Dictionary<string, List<string>>(StringComparer.Ordinal);
        for (var i = 1; i < args.Length; i++)
        {
            var name = args[i];
            if (!allowed.Contains(name, StringComparer.Ordinal))
            {
                throw new UsageException($"未知参数：{name}。");
            }

            if (options.ContainsKey(name) && name != "--url")
            {
                throw new UsageException($"参数不能重复：{name}。");
            }

            var value = "";
            if (name != "--overwrite")
            {
                if (++i >= args.Length || args[i].StartsWith("--", StringComparison.Ordinal)
                    || string.IsNullOrWhiteSpace(args[i]))
                {
                    throw new UsageException($"参数缺少值：{name}。");
                }

                value = args[i];
            }

            if (!options.TryGetValue(name, out var values))
            {
                values = [];
                options.Add(name, values);
            }

            values.Add(value);
        }

        var required = args[0] switch
        {
            "keygen" => new[] { "--private", "--public" },
            "manifest" => new[] { "--zip", "--version", "--channel", "--url", "--release-date", "--out" },
            "sign" => new[] { "--private", "--file" },
            _ => new[] { "--public", "--file" }
        };
        foreach (var name in required)
        {
            if (!options.ContainsKey(name))
            {
                throw new UsageException($"缺少必需参数：{name}。");
            }
        }

        if (options.TryGetValue("--channel", out var channels) && channels[0] is not ("stable" or "preview"))
        {
            throw new UsageException("通道只能是 stable 或 preview。");
        }

        return options;
    }

    private static string Value(Dictionary<string, List<string>> options, string name) => options[name][0];

    private static void Keygen(Dictionary<string, List<string>> options)
    {
        var privatePath = Path.GetFullPath(Value(options, "--private"));
        var publicPath = Path.GetFullPath(Value(options, "--public"));
        if (privatePath.Equals(publicPath, StringComparison.OrdinalIgnoreCase))
        {
            throw new UsageException("私钥和公钥必须使用不同文件。");
        }

        RefuseExisting(privatePath);
        RefuseExisting(publicPath);
        using var key = ECDsa.Create(ECCurve.NamedCurves.nistP256);
        using var privateFile = new FileStream(privatePath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        var publicCreated = false;
        try
        {
            using var publicFile = new FileStream(publicPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
            publicCreated = true;
            WriteUtf8(privateFile, key.ExportPkcs8PrivateKeyPem());
            WriteUtf8(publicFile, Convert.ToBase64String(key.ExportSubjectPublicKeyInfo()));
        }
        catch (Exception original)
        {
            privateFile.Dispose();
            try
            {
                File.Delete(privatePath);
                if (publicCreated)
                {
                    File.Delete(publicPath);
                }
            }
            catch (Exception cleanup) when (cleanup is IOException or UnauthorizedAccessException)
            {
                throw new UpdateException(UpdateErrorKind.FileSystem, "密钥生成失败，且无法清理本次创建的文件。",
                    new AggregateException(original, cleanup));
            }

            throw;
        }
    }

    private static void Manifest(Dictionary<string, List<string>> options)
    {
        var outPath = Value(options, "--out");
        RefuseExisting(outPath);
        using var zip = File.OpenRead(Value(options, "--zip"));
        if (zip.Length is < 1 or > UpdateManifestReader.MaxPackageBytes)
        {
            throw new UpdateException(UpdateErrorKind.InvalidManifest, "更新包大小必须在 1 字节到 4 GiB 之间。");
        }

        var document = new ManifestDocument
        {
            Schema = 1,
            Channel = Value(options, "--channel"),
            Version = Value(options, "--version"),
            ReleaseDate = Value(options, "--release-date"),
            MinUpdatableFrom = options.TryGetValue("--min-updatable-from", out var minimum) ? minimum[0] : "0.0.0",
            Notes = options.TryGetValue("--notes-file", out var notes) ? ReadNotes(notes[0]) : "",
            Package = new PackageDocument
            {
                Urls = options["--url"].ToArray(),
                Size = zip.Length,
                Sha256 = Convert.ToHexString(SHA256.HashData(zip)).ToLowerInvariant()
            }
        };
        var bytes = JsonSerializer.SerializeToUtf8Bytes(document, ManifestJsonContext.Default.ManifestDocument);
        UpdateManifestReader.ValidateUnsigned(bytes, document.Channel);
        using var file = new FileStream(outPath, FileMode.CreateNew, FileAccess.Write, FileShare.None);
        file.Write(bytes);
    }

    private static string ReadNotes(string path)
    {
        using var reader = new StreamReader(path, Encoding.UTF8, detectEncodingFromByteOrderMarks: true);
        var buffer = new char[16001];
        var length = 0;
        int count;
        while (length < buffer.Length && (count = reader.Read(buffer, length, buffer.Length - length)) > 0)
        {
            length += count;
        }

        if (length > 16000)
        {
            throw new UpdateException(UpdateErrorKind.InvalidManifest, "更新说明超过 16000 字符限制。");
        }

        return new string(buffer, 0, length);
    }

    private static void Sign(Dictionary<string, List<string>> options)
    {
        var path = Value(options, "--file");
        var signaturePath = path + ".sig";
        var overwrite = options.ContainsKey("--overwrite");
        if (!overwrite)
        {
            RefuseExisting(signaturePath);
        }

        using var key = ECDsa.Create();
        key.ImportFromPem(File.ReadAllText(Value(options, "--private")));
        UpdateSignature.RequireP256(key);
        using var input = File.OpenRead(path);
        if (input.Length > UpdateManifestReader.MaxManifestBytes)
        {
            throw new UpdateException(UpdateErrorKind.ManifestTooLarge, "待签名清单超过 64 KiB 限制。");
        }

        using var bytes = new MemoryStream();
        input.CopyTo(bytes);
        if (bytes.Length > UpdateManifestReader.MaxManifestBytes)
        {
            throw new UpdateException(UpdateErrorKind.ManifestTooLarge, "待签名清单超过 64 KiB 限制。");
        }

        var signature = UpdateSignature.Sign(bytes.ToArray(), key);
        using var output = new FileStream(signaturePath, overwrite ? FileMode.Create : FileMode.CreateNew,
            FileAccess.Write, FileShare.None);
        output.Write(signature);
    }

    private static async Task<UpdateManifest> VerifyAsync(Dictionary<string, List<string>> options)
    {
        var publicValue = Value(options, "--public");
        var publicKey = File.Exists(publicValue) ? File.ReadAllText(publicValue).Trim() : publicValue;
        var path = Value(options, "--file");
        using var file = File.OpenRead(path);
        using var signature = File.OpenRead(path + ".sig");
        var channel = options.TryGetValue("--channel", out var channels) ? channels[0] : "stable";
        return await UpdateManifestReader.ReadAsync(file, signature, [publicKey], channel).ConfigureAwait(false);
    }

    private static void RefuseExisting(string path)
    {
        if (File.Exists(path) || Directory.Exists(path))
        {
            throw new UpdateException(UpdateErrorKind.TargetExists, "目标文件或目录已存在，拒绝覆盖。");
        }
    }

    private static void WriteUtf8(Stream stream, string text) => stream.Write(Encoding.UTF8.GetBytes(text));

    private sealed class UsageException(string message) : Exception(message);
}
