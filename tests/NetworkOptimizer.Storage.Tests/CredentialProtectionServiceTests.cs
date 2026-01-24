using FluentAssertions;
using NetworkOptimizer.Storage.Services;
using Xunit;

namespace NetworkOptimizer.Storage.Tests;

public class CredentialProtectionServiceTests : IDisposable
{
    private readonly CredentialProtectionService _service;
    private readonly string _tempKeyDir;

    public CredentialProtectionServiceTests()
    {
        // Use a temp directory for the key file to isolate tests
        _tempKeyDir = Path.Combine(Path.GetTempPath(), $"credential_test_{Guid.NewGuid()}");
        Directory.CreateDirectory(_tempKeyDir);

        // Set environment to use temp directory
        var originalAppData = Environment.GetEnvironmentVariable("LOCALAPPDATA");
        Environment.SetEnvironmentVariable("LOCALAPPDATA", _tempKeyDir);

        _service = new CredentialProtectionService();

        // Restore original
        Environment.SetEnvironmentVariable("LOCALAPPDATA", originalAppData);
    }

    public void Dispose()
    {
        // Clean up temp directory
        if (Directory.Exists(_tempKeyDir))
        {
            try { Directory.Delete(_tempKeyDir, true); } catch { }
        }
    }

    #region Encrypt Tests

    [Fact]
    public void Encrypt_ValidPlaintext_ReturnsEncryptedString()
    {
        // Arrange
        var plaintext = "MySecretPassword123!";

        // Act
        var encrypted = _service.Encrypt(plaintext);

        // Assert
        encrypted.Should().StartWith("ENC:");
        encrypted.Should().NotBe(plaintext);
        encrypted.Length.Should().BeGreaterThan(plaintext.Length);
    }

    [Fact]
    public void Encrypt_EmptyString_ReturnsEmptyString()
    {
        // Arrange & Act
        var encrypted = _service.Encrypt("");

        // Assert
        encrypted.Should().BeEmpty();
    }

    [Fact]
    public void Encrypt_NullString_ReturnsNull()
    {
        // Arrange & Act
        var encrypted = _service.Encrypt(null!);

        // Assert
        encrypted.Should().BeNull();
    }

    [Fact]
    public void Encrypt_SameInputTwice_ProducesDifferentOutputs()
    {
        // Arrange
        var plaintext = "SamePassword";

        // Act
        var encrypted1 = _service.Encrypt(plaintext);
        var encrypted2 = _service.Encrypt(plaintext);

        // Assert - Different IVs should produce different ciphertext
        encrypted1.Should().NotBe(encrypted2);
    }

    [Fact]
    public void Encrypt_SpecialCharacters_HandlesCorrectly()
    {
        // Arrange
        var plaintext = "P@$$w0rd!#$%^&*()[]{}|;':\",./<>?`~";

        // Act
        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_UnicodeCharacters_HandlesCorrectly()
    {
        // Arrange
        var plaintext = "密码123パスワード🔐";

        // Act
        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Encrypt_LongPassword_HandlesCorrectly()
    {
        // Arrange
        var plaintext = new string('A', 10000); // 10KB password

        // Act
        var encrypted = _service.Encrypt(plaintext);
        var decrypted = _service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(plaintext);
    }

    #endregion

    #region Decrypt Tests

    [Fact]
    public void Decrypt_EncryptedString_ReturnsOriginalPlaintext()
    {
        // Arrange
        var plaintext = "MySecretPassword123!";
        var encrypted = _service.Encrypt(plaintext);

        // Act
        var decrypted = _service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Decrypt_EmptyString_ReturnsEmptyString()
    {
        // Arrange & Act
        var decrypted = _service.Decrypt("");

        // Assert
        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_NullString_ReturnsNull()
    {
        // Arrange & Act
        var decrypted = _service.Decrypt(null!);

        // Assert
        decrypted.Should().BeNull();
    }

    [Fact]
    public void Decrypt_PlaintextWithoutPrefix_ReturnsAsIs()
    {
        // Arrange - Simulating legacy unencrypted password
        var plaintext = "LegacyPassword123";

        // Act
        var decrypted = _service.Decrypt(plaintext);

        // Assert - Should return as-is for migration support
        decrypted.Should().Be(plaintext);
    }

    [Fact]
    public void Decrypt_InvalidEncryptedData_ReturnsEmpty()
    {
        // Arrange - ENC: prefix but invalid base64
        var invalid = "ENC:not-valid-base64!@#$";

        // Act
        var decrypted = _service.Decrypt(invalid);

        // Assert - Should return empty on decryption failure
        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_TruncatedEncryptedData_ReturnsEmpty()
    {
        // Arrange - Encrypt then truncate
        var encrypted = _service.Encrypt("SomePassword");
        var truncated = encrypted.Substring(0, encrypted.Length / 2);

        // Act
        var decrypted = _service.Decrypt(truncated);

        // Assert
        decrypted.Should().BeEmpty();
    }

    [Fact]
    public void Decrypt_TamperedCiphertext_ReturnsEmpty()
    {
        // Arrange
        var encrypted = _service.Encrypt("SecretPassword");
        var base64Part = encrypted.Substring(4);
        var bytes = Convert.FromBase64String(base64Part);

        // Tamper with the last byte to corrupt padding (CBC mode throws on invalid padding)
        bytes[bytes.Length - 1] ^= 0xFF;
        var tampered = "ENC:" + Convert.ToBase64String(bytes);

        // Act
        var decrypted = _service.Decrypt(tampered);

        // Assert - Should return empty on tampering (padding exception caught)
        decrypted.Should().BeEmpty();
    }

    #endregion

    #region IsEncrypted Tests

    [Fact]
    public void IsEncrypted_EncryptedString_ReturnsTrue()
    {
        // Arrange
        var encrypted = _service.Encrypt("Password");

        // Act
        var isEncrypted = _service.IsEncrypted(encrypted);

        // Assert
        isEncrypted.Should().BeTrue();
    }

    [Fact]
    public void IsEncrypted_PlaintextString_ReturnsFalse()
    {
        // Arrange
        var plaintext = "PlainPassword";

        // Act
        var isEncrypted = _service.IsEncrypted(plaintext);

        // Assert
        isEncrypted.Should().BeFalse();
    }

    [Fact]
    public void IsEncrypted_NullString_ReturnsFalse()
    {
        // Act
        var isEncrypted = _service.IsEncrypted(null);

        // Assert
        isEncrypted.Should().BeFalse();
    }

    [Fact]
    public void IsEncrypted_EmptyString_ReturnsFalse()
    {
        // Act
        var isEncrypted = _service.IsEncrypted("");

        // Assert
        isEncrypted.Should().BeFalse();
    }

    [Fact]
    public void IsEncrypted_StringStartingWithEncButNotEncrypted_ReturnsTrue()
    {
        // Arrange - This string happens to start with ENC: but is not actually encrypted
        var fake = "ENC:SomeRandomText";

        // Act
        var isEncrypted = _service.IsEncrypted(fake);

        // Assert - The method only checks the prefix, not validity
        isEncrypted.Should().BeTrue();
    }

    #endregion

    #region Roundtrip Tests

    [Theory]
    [InlineData("simple")]
    [InlineData("with spaces")]
    [InlineData("With123Numbers")]
    [InlineData("")]
    public void EncryptDecrypt_Roundtrip_PreservesData(string input)
    {
        // Act
        var encrypted = _service.Encrypt(input);
        var decrypted = _service.Decrypt(encrypted);

        // Assert
        decrypted.Should().Be(input);
    }

    [Fact]
    public void EncryptDecrypt_MultipleRoundtrips_AllPreserveData()
    {
        // Arrange
        var passwords = new[]
        {
            "Password1",
            "AnotherPassword",
            "ThirdPassword!@#",
            "日本語パスワード"
        };

        foreach (var password in passwords)
        {
            // Act
            var encrypted = _service.Encrypt(password);
            var decrypted = _service.Decrypt(encrypted);

            // Assert
            decrypted.Should().Be(password, because: $"roundtrip should preserve '{password}'");
        }
    }

    #endregion

    #region EnsureKeyExists Tests

    [Fact]
    public void EnsureKeyExists_DoesNotThrow()
    {
        // Act
        var act = () => _service.EnsureKeyExists();

        // Assert
        act.Should().NotThrow();
    }

    #endregion
}
