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

                    b.Property<string>("ReportDataJson")
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

                    b.Property<bool>("IsScheduled")
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

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.ClientSignalLog", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int?>("ApChannel")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("ApClientCount")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ApMac")
                        .HasMaxLength(17)
                        .HasColumnType("TEXT");

                    b.Property<string>("ApModel")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("ApName")
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<string>("ApRadioBand")
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<int?>("ApTxPower")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Band")
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<double?>("BottleneckLinkSpeedMbps")
                        .HasColumnType("REAL");

                    b.Property<int?>("Channel")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("ChannelWidth")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ClientIp")
                        .HasMaxLength(45)
                        .HasColumnType("TEXT");

                    b.Property<string>("ClientMac")
                        .IsRequired()
                        .HasMaxLength(17)
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .HasMaxLength(200)
                        .HasColumnType("TEXT");

                    b.Property<int?>("HopCount")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsMlo")
                        .HasColumnType("INTEGER");

                    b.Property<double?>("Latitude")
                        .HasColumnType("REAL");

                    b.Property<int?>("LocationAccuracyMeters")
                        .HasColumnType("INTEGER");

                    b.Property<double?>("Longitude")
                        .HasColumnType("REAL");

                    b.Property<string>("MloLinksJson")
                        .HasColumnType("TEXT");

                    b.Property<int?>("NoiseDbm")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Protocol")
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<long?>("RxRateKbps")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("SignalDbm")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("TEXT");

                    b.Property<string>("TraceHash")
                        .HasMaxLength(64)
                        .HasColumnType("TEXT");

                    b.Property<string>("TraceJson")
                        .HasColumnType("TEXT");

                    b.Property<long?>("TxRateKbps")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("TraceHash");

                    b.HasIndex("ClientMac", "Timestamp");

                    b.ToTable("ClientSignalLogs", (string)null);
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

                    b.Property<int>("TcMonitorPort")
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

                    b.Property<string>("Iperf3BinaryPath")
                        .HasMaxLength(500)
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

                    b.Property<string>("ClientMac")
                        .HasMaxLength(17)
                        .HasColumnType("TEXT");

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

                    b.Property<int>("Direction")
                        .HasColumnType("INTEGER");

                    b.Property<double>("DownloadBitsPerSecond")
                        .HasColumnType("REAL");

                    b.Property<long>("DownloadBytes")
                        .HasColumnType("INTEGER");

                    b.Property<double?>("DownloadJitterMs")
                        .HasColumnType("REAL");

                    b.Property<double?>("DownloadLatencyMs")
                        .HasColumnType("REAL");

                    b.Property<int>("DownloadRetransmits")
                        .HasColumnType("INTEGER");

                    b.Property<int>("DurationSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ErrorMessage")
                        .HasMaxLength(2000)
                        .HasColumnType("TEXT");

                    b.Property<double?>("JitterMs")
                        .HasColumnType("REAL");

                    b.Property<double?>("Latitude")
                        .HasColumnType("REAL");

                    b.Property<int?>("LocationAccuracyMeters")
                        .HasColumnType("INTEGER");

                    b.Property<string>("LocalIp")
                        .HasMaxLength(45)
                        .HasColumnType("TEXT");

                    b.Property<double?>("Longitude")
                        .HasColumnType("REAL");

                    b.Property<int>("ParallelStreams")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PathAnalysisJson")
                        .HasColumnType("TEXT");

                    b.Property<double?>("PingMs")
                        .HasColumnType("REAL");

                    b.Property<string>("RawDownloadJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("RawUploadJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("Notes")
                        .HasMaxLength(2000)
                        .HasColumnType("TEXT");

                    b.Property<bool>("Success")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("TestTime")
                        .HasColumnType("TEXT");

                    b.Property<double>("UploadBitsPerSecond")
                        .HasColumnType("REAL");

                    b.Property<long>("UploadBytes")
                        .HasColumnType("INTEGER");

                    b.Property<double?>("UploadJitterMs")
                        .HasColumnType("REAL");

                    b.Property<double?>("UploadLatencyMs")
                        .HasColumnType("REAL");

                    b.Property<int>("UploadRetransmits")
                        .HasColumnType("INTEGER");

                    b.Property<string>("UserAgent")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("WanName")
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("WanNetworkGroup")
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<int?>("WifiChannel")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("WifiNoiseDbm")
                        .HasColumnType("INTEGER");

                    b.Property<string>("WifiRadioProto")
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<string>("WifiRadio")
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<bool>("WifiIsMlo")
                        .HasColumnType("INTEGER");

                    b.Property<string>("WifiMloLinksJson")
                        .HasColumnType("TEXT");

                    b.Property<int?>("WifiSignalDbm")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("WifiTxRateKbps")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("WifiRxRateKbps")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("DeviceHost");

                    b.HasIndex("Direction");

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

                    b.Property<bool>("IgnoreControllerSSLErrors")
                        .HasColumnType("INTEGER");

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

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.SqmWanConfiguration", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double?>("BaselineLatencyMs")
                        .HasColumnType("REAL");

                    b.Property<int>("ConnectionType")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Interface")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<int>("NominalDownloadMbps")
                        .HasColumnType("INTEGER");

                    b.Property<int>("NominalUploadMbps")
                        .HasColumnType("INTEGER");

                    b.Property<string>("PingHost")
                        .IsRequired()
                        .HasMaxLength(255)
                        .HasColumnType("TEXT");

                    b.Property<int>("SpeedtestEveningHour")
                        .HasColumnType("INTEGER");

                    b.Property<int>("SpeedtestEveningMinute")
                        .HasColumnType("INTEGER");

                    b.Property<int>("SpeedtestMorningHour")
                        .HasColumnType("INTEGER");

                    b.Property<int>("SpeedtestMorningMinute")
                        .HasColumnType("INTEGER");

                    b.Property<string>("SpeedtestServerId")
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("WanNumber")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("WanNumber")
                        .IsUnique();

                    b.ToTable("SqmWanConfigurations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.AdminSettings", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Password")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("AdminSettings", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.UpnpNote", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("HostIp")
                        .IsRequired()
                        .HasMaxLength(45)
                        .HasColumnType("TEXT");

                    b.Property<string>("Port")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<string>("Protocol")
                        .IsRequired()
                        .HasMaxLength(10)
                        .HasColumnType("TEXT");

                    b.Property<string>("Note")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("HostIp", "Port", "Protocol")
                        .IsUnique();

                    b.ToTable("UpnpNotes", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.ApLocation", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("ApMac")
                        .IsRequired()
                        .HasMaxLength(17)
                        .HasColumnType("TEXT");

                    b.Property<double>("Latitude")
                        .HasColumnType("REAL");

                    b.Property<double>("Longitude")
                        .HasColumnType("REAL");

                    b.Property<int?>("Floor")
                        .HasColumnType("INTEGER");

                    b.Property<int>("OrientationDeg")
                        .HasColumnType("INTEGER");

                    b.Property<string>("MountType")
                        .HasMaxLength(20)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("ApMac")
                        .IsUnique();

                    b.ToTable("ApLocations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.Building", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<double>("CenterLatitude")
                        .HasColumnType("REAL");

                    b.Property<double>("CenterLongitude")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("Buildings", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.FloorPlan", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("BuildingId")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("FloorMaterial")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<int>("FloorNumber")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ImagePath")
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("Label")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<double>("NeLatitude")
                        .HasColumnType("REAL");

                    b.Property<double>("NeLongitude")
                        .HasColumnType("REAL");

                    b.Property<double>("Opacity")
                        .HasColumnType("REAL");

                    b.Property<double>("SwLatitude")
                        .HasColumnType("REAL");

                    b.Property<double>("SwLongitude")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("WallsJson")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("BuildingId");

                    b.ToTable("FloorPlans", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.FloorPlanImage", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("CropJson")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("FloorPlanId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ImagePath")
                        .IsRequired()
                        .HasMaxLength(500)
                        .HasColumnType("TEXT");

                    b.Property<string>("Label")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<double>("NeLatitude")
                        .HasColumnType("REAL");

                    b.Property<double>("NeLongitude")
                        .HasColumnType("REAL");

                    b.Property<double>("Opacity")
                        .HasColumnType("REAL");

                    b.Property<double>("RotationDeg")
                        .HasColumnType("REAL");

                    b.Property<int>("SortOrder")
                        .HasColumnType("INTEGER");

                    b.Property<double>("SwLatitude")
                        .HasColumnType("REAL");

                    b.Property<double>("SwLongitude")
                        .HasColumnType("REAL");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("FloorPlanId");

                    b.ToTable("FloorPlanImages", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.PlannedAp", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<string>("Model")
                        .IsRequired()
                        .HasMaxLength(50)
                        .HasColumnType("TEXT");

                    b.Property<double>("Latitude")
                        .HasColumnType("REAL");

                    b.Property<double>("Longitude")
                        .HasColumnType("REAL");

                    b.Property<int>("Floor")
                        .HasColumnType("INTEGER");

                    b.Property<int>("OrientationDeg")
                        .HasColumnType("INTEGER");

                    b.Property<string>("MountType")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("TEXT");

                    b.Property<int?>("TxPower24Dbm")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("TxPower5Dbm")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("TxPower6Dbm")
                        .HasColumnType("INTEGER");

                    b.Property<string>("AntennaMode")
                        .HasMaxLength(20)
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("PlannedAps", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.FloorPlan", b =>
                {
                    b.HasOne("NetworkOptimizer.Storage.Models.Building", "Building")
                        .WithMany("Floors")
                        .HasForeignKey("BuildingId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("Building");
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.FloorPlanImage", b =>
                {
                    b.HasOne("NetworkOptimizer.Storage.Models.FloorPlan", "FloorPlan")
                        .WithMany("Images")
                        .HasForeignKey("FloorPlanId")
                        .OnDelete(DeleteBehavior.Cascade)
                        .IsRequired();

                    b.Navigation("FloorPlan");
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.FloorPlan", b =>
                {
                    b.Navigation("Images");
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.Building", b =>
                {
                    b.Navigation("Floors");
                });

            modelBuilder.Entity("NetworkOptimizer.Alerts.Models.AlertRule", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("CooldownSeconds")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("DigestOnly")
                        .HasColumnType("INTEGER");

                    b.Property<int>("EscalationMinutes")
                        .HasColumnType("INTEGER");

                    b.Property<int>("EscalationSeverity")
                        .HasColumnType("INTEGER");

                    b.Property<string>("EventTypePattern")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("MinSeverity")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Source")
                        .HasColumnType("TEXT");

                    b.Property<string>("TargetDevices")
                        .HasColumnType("TEXT");

                    b.Property<double?>("ThresholdPercent")
                        .HasColumnType("REAL");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("AlertRules", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Alerts.Models.DeliveryChannel", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("ChannelType")
                        .HasColumnType("INTEGER");

                    b.Property<string>("ConfigJson")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<bool>("DigestEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<string>("DigestSchedule")
                        .HasColumnType("TEXT");

                    b.Property<bool>("IsEnabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("MinSeverity")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("DeliveryChannels", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Alerts.Models.AlertHistoryEntry", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("AcknowledgedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("ContextJson")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeliveredToChannels")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeliveryError")
                        .HasColumnType("TEXT");

                    b.Property<bool>("DeliverySucceeded")
                        .HasColumnType("INTEGER");

                    b.Property<string>("DeviceId")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceIp")
                        .HasColumnType("TEXT");

                    b.Property<string>("DeviceName")
                        .HasColumnType("TEXT");

                    b.Property<string>("EventType")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int?>("IncidentId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Message")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("ResolvedAt")
                        .HasColumnType("TEXT");

                    b.Property<int?>("RuleId")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Severity")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Source")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Status")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("TriggeredAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("IncidentId");

                    b.HasIndex("RuleId");

                    b.HasIndex("Status");

                    b.HasIndex("TriggeredAt");

                    b.HasIndex("Source", "TriggeredAt");

                    b.ToTable("AlertHistory", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Alerts.Models.AlertIncident", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("AlertCount")
                        .HasColumnType("INTEGER");

                    b.Property<string>("CorrelationKey")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FirstTriggeredAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastTriggeredAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("ResolvedAt")
                        .HasColumnType("TEXT");

                    b.Property<int>("Severity")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Status")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Title")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("CorrelationKey");

                    b.HasIndex("Status");

                    b.ToTable("AlertIncidents", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.CrowdSecReputation", b =>
                {
                    b.Property<string>("Ip")
                        .HasColumnType("TEXT");

                    b.Property<string>("ReputationJson")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("FetchedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("ExpiresAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Ip");

                    b.HasIndex("ExpiresAt");

                    b.ToTable("CrowdSecReputations", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.ThreatNoiseFilter", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("SourceIp")
                        .HasColumnType("TEXT");

                    b.Property<string>("DestIp")
                        .HasColumnType("TEXT");

                    b.Property<int?>("DestPort")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.ToTable("ThreatNoiseFilters", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.ThreatPattern", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<int>("PatternType")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("DetectedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("DedupKey")
                        .HasColumnType("TEXT");

                    b.Property<string>("SourceIpsJson")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int?>("TargetPort")
                        .HasColumnType("INTEGER");

                    b.Property<int>("EventCount")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("FirstSeen")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("LastSeen")
                        .HasColumnType("TEXT");

                    b.Property<double>("Confidence")
                        .HasColumnType("REAL");

                    b.Property<DateTime?>("LastAlertedAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("Description")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("PatternType", "DetectedAt");

                    b.ToTable("ThreatPatterns", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.ThreatEvent", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("TEXT");

                    b.Property<string>("SourceIp")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("SourcePort")
                        .HasColumnType("INTEGER");

                    b.Property<string>("DestIp")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("DestPort")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Protocol")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<long>("SignatureId")
                        .HasColumnType("INTEGER");

                    b.Property<string>("SignatureName")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Category")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<int>("Severity")
                        .HasColumnType("INTEGER");

                    b.Property<int>("Action")
                        .HasColumnType("INTEGER");

                    b.Property<string>("InnerAlertId")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("CountryCode")
                        .HasColumnType("TEXT");

                    b.Property<string>("City")
                        .HasColumnType("TEXT");

                    b.Property<int?>("Asn")
                        .HasColumnType("INTEGER");

                    b.Property<string>("AsnOrg")
                        .HasColumnType("TEXT");

                    b.Property<double?>("Latitude")
                        .HasColumnType("REAL");

                    b.Property<double?>("Longitude")
                        .HasColumnType("REAL");

                    b.Property<int>("KillChainStage")
                        .HasColumnType("INTEGER");

                    b.Property<int>("EventSource")
                        .HasColumnType("INTEGER");

                    b.Property<string>("Domain")
                        .HasColumnType("TEXT");

                    b.Property<string>("Direction")
                        .HasColumnType("TEXT");

                    b.Property<string>("Service")
                        .HasColumnType("TEXT");

                    b.Property<long?>("BytesTotal")
                        .HasColumnType("INTEGER");

                    b.Property<long?>("FlowDurationMs")
                        .HasColumnType("INTEGER");

                    b.Property<string>("NetworkName")
                        .HasColumnType("TEXT");

                    b.Property<string>("RiskLevel")
                        .HasColumnType("TEXT");

                    b.Property<int?>("PatternId")
                        .HasColumnType("INTEGER");

                    b.HasKey("Id");

                    b.HasIndex("Timestamp");

                    b.HasIndex("SourceIp", "Timestamp");

                    b.HasIndex("DestPort", "Timestamp");

                    b.HasIndex("KillChainStage");

                    b.HasIndex("EventSource");

                    b.HasIndex("InnerAlertId")
                        .IsUnique();

                    b.HasIndex("PatternId");

                    b.ToTable("ThreatEvents", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.ThreatEvent", b =>
                {
                    b.HasOne("NetworkOptimizer.Threats.Models.ThreatPattern", "Pattern")
                        .WithMany("Events")
                        .HasForeignKey("PatternId")
                        .OnDelete(DeleteBehavior.SetNull);

                    b.Navigation("Pattern");
                });

            modelBuilder.Entity("NetworkOptimizer.Threats.Models.ThreatPattern", b =>
                {
                    b.Navigation("Events");
                });

            modelBuilder.Entity("NetworkOptimizer.Alerts.Models.ScheduledTask", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("TaskType")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<int>("FrequencyMinutes")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("CustomMorningHour")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("CustomMorningMinute")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("CustomEveningHour")
                        .HasColumnType("INTEGER");

                    b.Property<int?>("CustomEveningMinute")
                        .HasColumnType("INTEGER");

                    b.Property<string>("TargetId")
                        .HasColumnType("TEXT");

                    b.Property<string>("TargetConfig")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("LastRunAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime?>("NextRunAt")
                        .HasColumnType("TEXT");

                    b.Property<string>("LastStatus")
                        .HasColumnType("TEXT");

                    b.Property<string>("LastErrorMessage")
                        .HasColumnType("TEXT");

                    b.Property<string>("LastResultSummary")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("TaskType");

                    b.HasIndex("Enabled");

                    b.HasIndex("NextRunAt");

                    b.ToTable("ScheduledTasks", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.WanDataUsageConfig", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("WanKey")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("TEXT");

                    b.Property<string>("Name")
                        .IsRequired()
                        .HasMaxLength(100)
                        .HasColumnType("TEXT");

                    b.Property<bool>("Enabled")
                        .HasColumnType("INTEGER");

                    b.Property<double>("ManualAdjustmentGb")
                        .HasColumnType("REAL");

                    b.Property<double>("DataCapGb")
                        .HasColumnType("REAL");

                    b.Property<int>("WarningThresholdPercent")
                        .HasColumnType("INTEGER");

                    b.Property<int>("BillingCycleDayOfMonth")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime>("CreatedAt")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("UpdatedAt")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("WanKey")
                        .IsUnique();

                    b.ToTable("WanDataUsageConfigs", (string)null);
                });

            modelBuilder.Entity("NetworkOptimizer.Storage.Models.WanDataUsageSnapshot", b =>
                {
                    b.Property<int>("Id")
                        .ValueGeneratedOnAdd()
                        .HasColumnType("INTEGER");

                    b.Property<string>("WanKey")
                        .IsRequired()
                        .HasMaxLength(20)
                        .HasColumnType("TEXT");

                    b.Property<long>("RxBytes")
                        .HasColumnType("INTEGER");

                    b.Property<long>("TxBytes")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsCounterReset")
                        .HasColumnType("INTEGER");

                    b.Property<bool>("IsBaseline")
                        .HasColumnType("INTEGER");

                    b.Property<DateTime?>("GatewayBootTime")
                        .HasColumnType("TEXT");

                    b.Property<DateTime>("Timestamp")
                        .HasColumnType("TEXT");

                    b.HasKey("Id");

                    b.HasIndex("WanKey", "Timestamp");

                    b.ToTable("WanDataUsageSnapshots", (string)null);
                });
#pragma warning restore 612, 618
        }
    }
}
