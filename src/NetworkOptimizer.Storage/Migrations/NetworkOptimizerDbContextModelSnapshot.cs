using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Infrastructure;
using NetworkOptimizer.Storage.Models;

#nullable disable

namespace NetworkOptimizer.Storage.Migrations
{
    [DbContext(typeof(NetworkOptimizerDbContext))]
    partial class NetworkOptimizerDbContextModelSnapshot : ModelSnapshot
    {
        protected override void BuildModel(ModelBuilder modelBuilder)
        {
#pragma warning disable 612, 618
            modelBuilder.HasAnnotation("ProductVersion", "9.0.0");

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.AuditResult", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("AuditVersion")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("AuditDate")
                        .HasColumnType("TEXT");

                    b.Property<double>("ComplianceScore")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceId")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<int>("FailedChecks")
                        .HasColumnType("INTEGER");

                    b.Property<string>("FindingsJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("FirmwareVersion")
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<string>("Model")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<int>("PassedChecks")
                        .HasColumnType("INTEGER");

                    b.Property<int>("TotalChecks")
                        .HasColumnType("INTEGER");

                    b.Property<int>("WarningChecks")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("DeviceId");

                    b.HasIndex("AuditDate");

                    b.HasIndex("DeviceId", "AuditDate");

                    b.ToTable("AuditResults", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.SqmBaseline", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<long>("AvgBytesIn")
                        .HasColumnType("INTEGER");

                    b.Property<long>("AvgBytesOut")
                        .HasColumnType("INTEGER");

                    b.Property<double>("AvgJitter")
                        .HasColumnType("REAL");

                    b.Property<double>("AvgLatency")
                        .HasColumnType("REAL");

                    b.Property<double>("AvgPacketLoss")
                        .HasColumnType("REAL");

                    b.Property<double>("AvgUtilization")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("BaselineEnd")
                        .HasColumnType("TEXT");

                    b.Property<int>("BaselineHours")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("BaselineStart")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceId")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("HourlyDataJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("InterfaceId")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("InterfaceName")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<double>("MaxJitter")
                        .HasColumnType("REAL");

                    b.Property<double>("MaxPacketLoss")
                        .HasColumnType("REAL");

                    b.Property<long>("MedianBytesIn")
                        .HasColumnType("INTEGER");

                    b.Property<long>("MedianBytesOut")
                        .HasColumnType("INTEGER");

                    b.Property<double>("P95Latency")
                        .HasColumnType("REAL");

                    b.Property<double>("P99Latency")
                        .HasColumnType("REAL");

                    b.Property<long>("PeakBytesIn")
                        .HasColumnType("INTEGER");

                    b.Property<long>("PeakBytesOut")
                        .HasColumnType("INTEGER");

                    b.Property<double>("PeakLatency")
                        .HasColumnType("REAL");

                    b.Property<double>("PeakUtilization")
                        .HasColumnType("REAL");

                    b.Property<double>("RecommendedDownloadMbps")
                        .HasColumnType("REAL");

                    b.Property<double>("RecommendedUploadMbps")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("DeviceId");

                    b.HasIndex("InterfaceId");

                    b.HasIndex("BaselineStart");

                    b.HasIndex("DeviceId", "InterfaceId")
                        .IsUnique();

                    b.ToTable("SqmBaselines", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.AgentConfiguration", b =>
                {
                    b.Property<string>("AgentId")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("AdditionalSettingsJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("AgentName")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<bool>("AuditEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("AuditIntervalHours")
                        .HasColumnType("INTEGER");

                    b.Property<int>("BatchSize")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceType")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceUrl")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<int>("FlushIntervalSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("LastSeenAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("MetricsEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("PollingIntervalSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("SqmEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("AgentId");

                    b.HasIndex("IsEnabled");

                    b.HasIndex("LastSeenAt");

                    b.ToTable("AgentConfigurations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.LicenseInfo", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("ExpirationDate")
                        .HasColumnType("TEXT");

                    b.Property<string>("FeaturesJson")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsActive")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("IssueDate")
                        .HasColumnType("TEXT");

                    b.Property<string>("LicenseKey")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("LicenseType")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("LicensedTo")
                        .IsRequired()
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<int>("MaxAgents")
                        .HasColumnType("INTEGER");

                    b.Property<int>("MaxDevices")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Organization")
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("IsActive");

                    b.HasIndex("ExpirationDate");

                    b.HasIndex("LicenseKey")
                        .IsUnique();

                    b.ToTable("Licenses", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.UniFiSshSettings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Host")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<string>("Password")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<int>("Port")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PrivateKeyPath")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("UniFiSshSettings", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.GatewaySshSettings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Host")
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<int>("Iperf3Port")
                        .HasColumnType("INTEGER");

                    b.Property<string>("LastTestResult")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("LastTestedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Password")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<int>("Port")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PrivateKeyPath")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("GatewaySshSettings", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.DismissedIssue", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("IssueKey")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("DismissedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("IssueKey")
                        .IsUnique();

                    b.ToTable("DismissedIssues", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.ModemConfiguration", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Host")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<string>("LastError")
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("LastPolled")
                        .HasColumnType("TEXT");

                    b.Property<string>("ModemType")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("Password")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<int>("PollingIntervalSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Port")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PrivateKeyPath")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("QmiDevice")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Username")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("Host");

                    b.HasIndex("Enabled");

                    b.ToTable("ModemConfigurations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.DeviceSshConfiguration", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceType")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Host")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("SshPassword")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("SshPrivateKeyPath")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("SshUsername")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<bool>("StartIperf3Server")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("Host");

                    b.HasIndex("Enabled");

                    b.ToTable("DeviceSshConfigurations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.Iperf3Result", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("DeviceHost")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceType")
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<double>("DownloadBitsPerSecond")
                        .HasColumnType("REAL");

                    b.Property<long>("DownloadBytes")
                        .HasColumnType("INTEGER");

                    b.Property<int>("DownloadRetransmits")
                        .HasColumnType("INTEGER");

                    b.Property<int>("DurationSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ErrorMessage")
                        .HasMaxLength(2000)
                        .HasColumnType("TEXT");

                    b.Property<int>("ParallelStreams")
                        .HasColumnType("INTEGER");

                    b.Property<string>("RawDownloadJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("RawUploadJson")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Success")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("TestTime")
                        .HasColumnType("TEXT");

                    b.Property<double>("UploadBitsPerSecond")
                        .HasColumnType("REAL");

                    b.Property<long>("UploadBytes")
                        .HasColumnType("INTEGER");

                    b.Property<int>("UploadRetransmits")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("DeviceHost");

                    b.HasIndex("TestTime");

                    b.HasIndex("DeviceHost", "TestTime");

                    b.ToTable("Iperf3Results", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.SystemSetting", b =>
                {
                    b.Property<string>("Key")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("Value")
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Key");

                    b.ToTable("SystemSettings", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.UniFiConnectionSettings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("ControllerUrl")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsConfigured")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("LastConnectedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("LastError")
                        .HasMaxLength(1000)
                        .HasColumnType("TEXT");

                    b.Property<string>("Password")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<bool>("RememberCredentials")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Site")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Username")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("UniFiConnectionSettings", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
