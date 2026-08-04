using Microsoft.AspNetCore.Identity;
using Microsoft.AspNetCore.Identity.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore;

namespace NetworkOptimizer.Storage.Models.Identity;

/// <summary>
/// Dedicated ASP.NET Core Identity context for users, roles, RBAC, federation config, and the audit
/// log. Deliberately SEPARATE from <see cref="NetworkOptimizerDbContext"/>: that context is
/// site-routed (its connection points at the current site's database file), whereas identity and
/// audit data must live only in the MAIN database (design docs 02, 04, 05). Both contexts share the
/// main SQLite file at runtime, so this context uses its own migrations-history table
/// (<see cref="MigrationsHistoryTable"/>) to keep the two migration streams independent.
/// </summary>
public class AuthDbContext : IdentityDbContext<ApplicationUser, ApplicationRole, string>
{
    /// <summary>Migrations-history table name for this context (kept distinct from the product context's default history table).</summary>
    public const string MigrationsHistoryTable = "__AuthMigrationsHistory";

    public AuthDbContext(DbContextOptions<AuthDbContext> options)
        : base(options)
    {
    }

    /// <summary>Per-site (or per-group, or all-sites) role grants.</summary>
    public DbSet<SiteMembership> SiteMemberships { get; set; }

    /// <summary>Named collections of sites for scalable RBAC.</summary>
    public DbSet<SiteGroup> SiteGroups { get; set; }

    /// <summary>Membership of sites in <see cref="SiteGroup"/>s.</summary>
    public DbSet<SiteGroupMember> SiteGroupMembers { get; set; }

    /// <summary>Configured OIDC/SAML providers.</summary>
    public DbSet<FederationProvider> FederationProviders { get; set; }

    /// <summary>IdP group/claim to global-role mappings.</summary>
    public DbSet<FederationRoleMapping> FederationRoleMappings { get; set; }

    /// <summary>IdP group/claim to site-membership mappings.</summary>
    public DbSet<FederationSiteMapping> FederationSiteMappings { get; set; }

    /// <summary>Append-only audit log (main DB, site-filterable via <see cref="AuditEvent.SiteSlug"/>).</summary>
    public DbSet<AuditEvent> AuditEvents { get; set; }

    /// <summary>WebAuthn passkey credentials (.NET 10 Identity passkey store; design doc 02).</summary>
    public DbSet<IdentityUserPasskey<string>> Passkeys { get; set; }

    /// <summary>Per-user counts of teaching hints shown, so a hint can retire once it is learned.</summary>
    public DbSet<UserUiHint> UserUiHints { get; set; }

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        base.OnModelCreating(modelBuilder);

        // .NET 10 Identity passkey credential store. The base model does not map it by default in this
        // version, so map it explicitly (design doc 02 - passkeys).
        modelBuilder.Entity<IdentityUserPasskey<string>>(entity =>
        {
            entity.ToTable("AspNetUserPasskeys");
            entity.HasKey(p => p.CredentialId);
            entity.HasIndex(p => p.UserId);
            // The credential's binary + metadata payload is stored as a JSON column.
            entity.OwnsOne(p => p.Data, d => d.ToJson());
        });

        // One row per user per hint - the upsert relies on it, and a duplicate would let a hint
        // count twice as slowly and outstay its welcome.
        modelBuilder.Entity<UserUiHint>(entity =>
        {
            entity.HasIndex(h => new { h.UserId, h.HintKey }).IsUnique();
        });

        modelBuilder.Entity<ApplicationUser>(entity =>
        {
            entity.Property(e => e.DisplayName).HasMaxLength(200);
            entity.Property(e => e.LastLoginMethod).HasMaxLength(64);
        });

        modelBuilder.Entity<ApplicationRole>(entity =>
        {
            entity.Property(e => e.Description).HasMaxLength(500);
        });

        modelBuilder.Entity<SiteMembership>(entity =>
        {
            entity.ToTable("SiteMemberships");
            entity.Property(e => e.TargetId).HasMaxLength(64);
            entity.HasIndex(e => e.UserId);
            entity.HasIndex(e => new { e.UserId, e.TargetType, e.TargetId }).IsUnique();
            entity.HasOne<ApplicationUser>()
                .WithMany()
                .HasForeignKey(e => e.UserId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<SiteGroup>(entity =>
        {
            entity.ToTable("SiteGroups");
            entity.Property(e => e.Name).HasMaxLength(100).IsRequired();
            entity.HasIndex(e => e.Name).IsUnique();
        });

        modelBuilder.Entity<SiteGroupMember>(entity =>
        {
            entity.ToTable("SiteGroupMembers");
            entity.Property(e => e.SiteSlug).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => new { e.GroupId, e.SiteSlug }).IsUnique();
            entity.HasIndex(e => e.SiteSlug);
            entity.HasOne<SiteGroup>()
                .WithMany()
                .HasForeignKey(e => e.GroupId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FederationProvider>(entity =>
        {
            entity.ToTable("FederationProviders");
            entity.Property(e => e.Scheme).HasMaxLength(64).IsRequired();
            entity.Property(e => e.DisplayName).HasMaxLength(200).IsRequired();
            entity.Property(e => e.ButtonLabel).HasMaxLength(200);
            entity.HasIndex(e => e.Scheme).IsUnique();
            entity.HasMany(e => e.RoleMappings)
                .WithOne()
                .HasForeignKey(m => m.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
            entity.HasMany(e => e.SiteMappings)
                .WithOne()
                .HasForeignKey(m => m.ProviderId)
                .OnDelete(DeleteBehavior.Cascade);
        });

        modelBuilder.Entity<FederationRoleMapping>(entity =>
        {
            entity.ToTable("FederationRoleMappings");
            entity.Property(e => e.GroupOrClaimValue).HasMaxLength(256).IsRequired();
            entity.Property(e => e.GlobalRole).HasMaxLength(64).IsRequired();
            entity.HasIndex(e => e.ProviderId);
        });

        modelBuilder.Entity<FederationSiteMapping>(entity =>
        {
            entity.ToTable("FederationSiteMappings");
            entity.Property(e => e.GroupOrClaimValue).HasMaxLength(256).IsRequired();
            entity.Property(e => e.TargetValue).HasMaxLength(100);
            entity.HasIndex(e => e.ProviderId);
        });

        modelBuilder.Entity<AuditEvent>(entity =>
        {
            entity.ToTable("AuditEvents");
            entity.Property(e => e.Category).HasMaxLength(32).IsRequired();
            entity.Property(e => e.Action).HasMaxLength(64).IsRequired();
            entity.Property(e => e.Outcome).HasMaxLength(16).IsRequired();
            entity.Property(e => e.ActorName).HasMaxLength(256);
            entity.Property(e => e.ActorAuthMethod).HasMaxLength(64);
            entity.Property(e => e.SourceIp).HasMaxLength(64);
            entity.Property(e => e.UserAgent).HasMaxLength(512);
            entity.Property(e => e.TargetType).HasMaxLength(64);
            entity.Property(e => e.TargetId).HasMaxLength(256);
            entity.Property(e => e.TargetName).HasMaxLength(256);
            entity.Property(e => e.SiteSlug).HasMaxLength(64);
            entity.Property(e => e.CorrelationId).HasMaxLength(64);
            entity.HasIndex(e => e.TimestampUtc);
            entity.HasIndex(e => e.Category);
            entity.HasIndex(e => e.ActorUserId);
            entity.HasIndex(e => e.SiteSlug);
            entity.HasIndex(e => e.Action);
        });
    }
}
