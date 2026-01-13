using System.Text;
using System.Text.Json;
using FluentAssertions;
using NetworkOptimizer.UniFi.Models;
using Xunit;

namespace NetworkOptimizer.UniFi.Tests;

public class UniFiFingerprintDatabaseTests
{
    #region Default Values Tests

    [Fact]
    public void UniFiFingerprintDatabase_DefaultValues_AreCorrect()
    {
        // Act
        var db = new UniFiFingerprintDatabase();

        // Assert
        db.DevTypeIds.Should().NotBeNull().And.BeEmpty();
        db.FamilyIds.Should().NotBeNull().And.BeEmpty();
        db.VendorIds.Should().NotBeNull().And.BeEmpty();
        db.OsClassIds.Should().NotBeNull().And.BeEmpty();
        db.OsNameIds.Should().NotBeNull().And.BeEmpty();
        db.DevIds.Should().NotBeNull().And.BeEmpty();
    }

    #endregion

    #region GetDeviceTypeName Tests

    [Fact]
    public void GetDeviceTypeName_ExistingId_ReturnsName()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevTypeIds["9"] = "IP Network Camera";
        db.DevTypeIds["42"] = "Smart Plug";

        // Act & Assert
        db.GetDeviceTypeName(9).Should().Be("IP Network Camera");
        db.GetDeviceTypeName(42).Should().Be("Smart Plug");
    }

    [Fact]
    public void GetDeviceTypeName_NonExistingId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevTypeIds["9"] = "IP Network Camera";

        // Act & Assert
        db.GetDeviceTypeName(999).Should().BeNull();
    }

    [Fact]
    public void GetDeviceTypeName_NullId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevTypeIds["9"] = "IP Network Camera";

        // Act & Assert
        db.GetDeviceTypeName(null).Should().BeNull();
    }

    #endregion

    #region GetFamilyName Tests

    [Fact]
    public void GetFamilyName_ExistingId_ReturnsName()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.FamilyIds["5"] = "Intelligent Home Appliances";
        db.FamilyIds["7"] = "Network & Peripheral";

        // Act & Assert
        db.GetFamilyName(5).Should().Be("Intelligent Home Appliances");
        db.GetFamilyName(7).Should().Be("Network & Peripheral");
    }

    [Fact]
    public void GetFamilyName_NonExistingId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.FamilyIds["5"] = "Intelligent Home Appliances";

        // Act & Assert
        db.GetFamilyName(999).Should().BeNull();
    }

    [Fact]
    public void GetFamilyName_NullId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();

        // Act & Assert
        db.GetFamilyName(null).Should().BeNull();
    }

    #endregion

    #region GetVendorName Tests

    [Fact]
    public void GetVendorName_ExistingId_ReturnsName()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.VendorIds["244"] = "Amazon";
        db.VendorIds["232"] = "Ring";

        // Act & Assert
        db.GetVendorName(244).Should().Be("Amazon");
        db.GetVendorName(232).Should().Be("Ring");
    }

    [Fact]
    public void GetVendorName_NonExistingId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.VendorIds["244"] = "Amazon";

        // Act & Assert
        db.GetVendorName(999).Should().BeNull();
    }

    [Fact]
    public void GetVendorName_NullId_ReturnsNull()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();

        // Act & Assert
        db.GetVendorName(null).Should().BeNull();
    }

    #endregion

    #region Merge Tests

    [Fact]
    public void Merge_EmptyOther_NoChanges()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevTypeIds["1"] = "Camera";
        var other = new UniFiFingerprintDatabase();

        // Act
        db.Merge(other);

        // Assert
        db.DevTypeIds.Should().HaveCount(1);
        db.DevTypeIds["1"].Should().Be("Camera");
    }

    [Fact]
    public void Merge_NewEntries_AddsAll()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        var other = new UniFiFingerprintDatabase
        {
            DevTypeIds = { ["1"] = "Camera", ["2"] = "Smart Light" },
            FamilyIds = { ["5"] = "Home" },
            VendorIds = { ["100"] = "Vendor A" },
            OsClassIds = { ["10"] = "Linux" },
            OsNameIds = { ["20"] = "Ubuntu" }
        };

        // Act
        db.Merge(other);

        // Assert
        db.DevTypeIds.Should().HaveCount(2);
        db.DevTypeIds["1"].Should().Be("Camera");
        db.DevTypeIds["2"].Should().Be("Smart Light");
        db.FamilyIds.Should().HaveCount(1);
        db.FamilyIds["5"].Should().Be("Home");
        db.VendorIds.Should().HaveCount(1);
        db.OsClassIds.Should().HaveCount(1);
        db.OsNameIds.Should().HaveCount(1);
    }

    [Fact]
    public void Merge_DuplicateKeys_DoesNotOverwrite()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevTypeIds["1"] = "Original Camera";
        db.VendorIds["100"] = "Original Vendor";

        var other = new UniFiFingerprintDatabase
        {
            DevTypeIds = { ["1"] = "New Camera", ["2"] = "New Device" },
            VendorIds = { ["100"] = "New Vendor" }
        };

        // Act
        db.Merge(other);

        // Assert - TryAdd does not overwrite existing
        db.DevTypeIds["1"].Should().Be("Original Camera");
        db.DevTypeIds["2"].Should().Be("New Device");
        db.VendorIds["100"].Should().Be("Original Vendor");
    }

    [Fact]
    public void Merge_DevIds_MergesCorrectly()
    {
        // Arrange
        var db = new UniFiFingerprintDatabase();
        db.DevIds["1"] = new FingerprintDeviceEntry { Name = "Device 1" };

        var other = new UniFiFingerprintDatabase();
        other.DevIds["1"] = new FingerprintDeviceEntry { Name = "Different Device 1" };
        other.DevIds["2"] = new FingerprintDeviceEntry { Name = "Device 2" };

        // Act
        db.Merge(other);

        // Assert
        db.DevIds.Should().HaveCount(2);
        db.DevIds["1"].Name.Should().Be("Device 1");  // Original preserved
        db.DevIds["2"].Name.Should().Be("Device 2");  // New added
    }

    #endregion

    #region FingerprintDeviceEntry Tests

    [Fact]
    public void FingerprintDeviceEntry_DefaultValues_AreCorrect()
    {
        // Act
        var entry = new FingerprintDeviceEntry();

        // Assert
        entry.DevTypeId.Should().BeNull();
        entry.FamilyId.Should().BeNull();
        entry.VendorId.Should().BeNull();
        entry.Name.Should().BeEmpty();
        entry.OsClassId.Should().BeNull();
        entry.OsNameId.Should().BeNull();
        entry.FbId.Should().BeNull();
        entry.TmId.Should().BeNull();
        entry.CtagId.Should().BeNull();
    }

    [Fact]
    public void FingerprintDeviceEntry_CanSetAllProperties()
    {
        // Act
        var entry = new FingerprintDeviceEntry
        {
            DevTypeId = "9",
            FamilyId = "5",
            VendorId = "244",
            Name = "Echo Dot",
            OsClassId = "10",
            OsNameId = "20",
            FbId = "123",
            TmId = "456",
            CtagId = "789"
        };

        // Assert
        entry.DevTypeId.Should().Be("9");
        entry.FamilyId.Should().Be("5");
        entry.VendorId.Should().Be("244");
        entry.Name.Should().Be("Echo Dot");
        entry.OsClassId.Should().Be("10");
        entry.OsNameId.Should().Be("20");
        entry.FbId.Should().Be("123");
        entry.TmId.Should().Be("456");
        entry.CtagId.Should().Be("789");
    }

    #endregion

    #region StringOrNumberConverter Tests

    [Theory]
    [InlineData("\"123\"", "123")]
    [InlineData("\"abc\"", "abc")]
    [InlineData("\"\"", "")]
    public void StringOrNumberConverter_ReadString_ReturnsString(string json, string expected)
    {
        // Arrange
        var converter = new StringOrNumberConverter();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        // Act
        var result = converter.Read(ref reader, typeof(string), new JsonSerializerOptions());

        // Assert
        result.Should().Be(expected);
    }

    [Theory]
    [InlineData("123", "123")]
    [InlineData("0", "0")]
    [InlineData("-456", "-456")]
    [InlineData("9999999999", "9999999999")]
    public void StringOrNumberConverter_ReadNumber_ReturnsString(string json, string expected)
    {
        // Arrange
        var converter = new StringOrNumberConverter();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes(json));
        reader.Read();

        // Act
        var result = converter.Read(ref reader, typeof(string), new JsonSerializerOptions());

        // Assert
        result.Should().Be(expected);
    }

    [Fact]
    public void StringOrNumberConverter_ReadNull_ReturnsNull()
    {
        // Arrange
        var converter = new StringOrNumberConverter();
        var reader = new Utf8JsonReader(Encoding.UTF8.GetBytes("null"));
        reader.Read();

        // Act
        var result = converter.Read(ref reader, typeof(string), new JsonSerializerOptions());

        // Assert
        result.Should().BeNull();
    }

    [Fact]
    public void StringOrNumberConverter_ReadInvalidToken_ThrowsJsonException()
    {
        // Arrange & Act
        var converter = new StringOrNumberConverter();
        var bytes = Encoding.UTF8.GetBytes("true");
        var reader = new Utf8JsonReader(bytes);
        reader.Read();

        // Act & Assert
        Exception? caughtException = null;
        try
        {
            converter.Read(ref reader, typeof(string), new JsonSerializerOptions());
        }
        catch (JsonException ex)
        {
            caughtException = ex;
        }

        caughtException.Should().NotBeNull();
        caughtException.Should().BeOfType<JsonException>();
        caughtException!.Message.Should().Contain("Unexpected token type");
    }

    [Fact]
    public void StringOrNumberConverter_WriteString_WritesCorrectJson()
    {
        // Arrange
        var converter = new StringOrNumberConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        // Act
        converter.Write(writer, "test", new JsonSerializerOptions());
        writer.Flush();

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        json.Should().Be("\"test\"");
    }

    [Fact]
    public void StringOrNumberConverter_WriteNull_WritesNull()
    {
        // Arrange
        var converter = new StringOrNumberConverter();
        using var stream = new MemoryStream();
        using var writer = new Utf8JsonWriter(stream);

        // Act
        converter.Write(writer, null, new JsonSerializerOptions());
        writer.Flush();

        // Assert
        var json = Encoding.UTF8.GetString(stream.ToArray());
        json.Should().Be("null");
    }

    [Fact]
    public void StringOrNumberConverter_Deserialization_WorksWithModel()
    {
        // Arrange - JSON with mixed string and number values
        var json = """
        {
            "dev_type_id": "9",
            "family_id": 5,
            "vendor_id": "244",
            "name": "Echo Dot"
        }
        """;

        // Act
        var entry = JsonSerializer.Deserialize<FingerprintDeviceEntry>(json);

        // Assert
        entry.Should().NotBeNull();
        entry!.DevTypeId.Should().Be("9");
        entry.FamilyId.Should().Be("5");  // Number converted to string
        entry.VendorId.Should().Be("244");
        entry.Name.Should().Be("Echo Dot");
    }

    #endregion
}
