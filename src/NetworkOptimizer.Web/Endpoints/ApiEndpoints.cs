namespace NetworkOptimizer.Web.Endpoints;

/// <summary>
/// The single place every minimal-API endpoint is mapped. Program.cs calls <see cref="MapAll"/> and
/// nothing else, and architecture test A1 walks the endpoints this produces to prove each one either
/// carries authorization metadata or lives under <c>/api/public/</c> (design doc 06, gate 2/3). A new
/// endpoint that is not registered here is not reachable, so it cannot slip past the test.
/// </summary>
public static class ApiEndpoints
{
    /// <summary>Maps every API endpoint group.</summary>
    public static void MapAll(WebApplication app)
    {
        // Identity and federation surfaces (sign-in, passkey ceremonies, IdP callbacks). These
        // declare their own authorization: the sign-in paths must stay reachable while anonymous.
        app.MapAuthEndpoints();
        app.MapPasskeyEndpoints();
        app.MapFederationEndpoints();
        app.MapSamlEndpoints();
        app.MapAuditLogEndpoints();

        HealthEndpoints.Map(app);

        // Product APIs.
        app.MapAlertEndpoints();
        app.MapSpeedTestEndpoints();
        ReportEndpoints.Map(app);
        UpnpEndpoints.Map(app);
        ApLocationEndpoints.Map(app);
        FloorPlanEndpoints.Map(app);
        ClientDashboardEndpoints.Map(app);
        DemoMappingEndpoints.Map(app);
        ConfigTransferEndpoints.Map(app);
        LanFlowMapEndpoints.Map(app);
        SiteAgentEndpoints.Map(app);
        MonitoringChartEndpoints.Map(app);
        IspHealthEndpoints.Map(app);
        FirmwareRolloutEndpoints.Map(app);
        MonitoringInvestigateEndpoints.Map(app);
        FlakyTargetEndpoints.Map(app);
        DeviceHealthChartEndpoints.Map(app);
        PortStatsEndpoints.Map(app);
        SfpChartEndpoints.Map(app);
        CellularChartEndpoints.Map(app);
        CmChartEndpoints.Map(app);
        OntChartEndpoints.Map(app);
        StarlinkChartEndpoints.Map(app);
        SnmpEndpoints.Map(app);
        SupportFileEndpoints.Map(app);
    }
}
