using Microsoft.EntityFrameworkCore;

namespace AnythingCanBeFarming.Data;

public sealed class AcbfDbContext(DbContextOptions<AcbfDbContext> options) : DbContext(options)
{
    public DbSet<WfoTaxon> WfoTaxa => Set<WfoTaxon>();
    public DbSet<WfoImport> WfoImports => Set<WfoImport>();
    public DbSet<WfoIpniMapping> WfoIpniMappings => Set<WfoIpniMapping>();
    public DbSet<WfoDeprecatedName> WfoDeprecatedNames => Set<WfoDeprecatedName>();
    public DbSet<WfoDeduplicatedId> WfoDeduplicatedIds => Set<WfoDeduplicatedId>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        var imports = modelBuilder.Entity<WfoImport>();
        imports.ToTable("wfo_import", "reference");
        imports.HasKey(x => x.Id);
        imports.Property(x => x.Id).UseIdentityByDefaultColumn();
        imports.Property(x => x.ValidationJson).HasColumnType("jsonb");
        imports.HasIndex(x => x.SourceFileHash);
        imports.Property(x => x.DatasetKind).HasDefaultValue("Backbone");

        var ipni = modelBuilder.Entity<WfoIpniMapping>();
        ipni.ToTable("wfo_ipni_mapping", "reference");
        ipni.Property(x => x.Id).UseIdentityByDefaultColumn();
        ipni.HasIndex(x => new { x.IpniId, x.WfoId }).IsUnique();
        ipni.HasIndex(x => x.IpniId);
        ipni.HasIndex(x => x.NormalizedIpniId);
        ipni.HasIndex(x => x.WfoId);
        ipni.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.WfoTaxonId).OnDelete(DeleteBehavior.Restrict);
        ipni.HasOne<WfoImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var deprecated = modelBuilder.Entity<WfoDeprecatedName>();
        deprecated.ToTable("wfo_deprecated_name", "reference");
        deprecated.Property(x => x.Id).UseIdentityByDefaultColumn();
        deprecated.HasIndex(x => x.WfoId).IsUnique();
        deprecated.HasIndex(x => x.CanonicalName);
        deprecated.HasIndex(x => x.Rank);
        deprecated.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.WfoTaxonId).OnDelete(DeleteBehavior.Restrict);
        deprecated.HasOne<WfoImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var deduplicated = modelBuilder.Entity<WfoDeduplicatedId>();
        deduplicated.ToTable("wfo_deduplicated_id", "reference");
        deduplicated.Property(x => x.Id).UseIdentityByDefaultColumn();
        deduplicated.HasIndex(x => x.DeprecatedWfoId).IsUnique();
        deduplicated.HasIndex(x => x.ReplacementWfoId);
        deduplicated.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.DeprecatedTaxonId).OnDelete(DeleteBehavior.Restrict);
        deduplicated.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.ReplacementTaxonId).OnDelete(DeleteBehavior.Restrict);
        deduplicated.HasOne<WfoImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var taxon = modelBuilder.Entity<WfoTaxon>();
        taxon.ToTable("wfo_taxon", "reference");
        taxon.HasKey(x => x.Id);
        taxon.Property(x => x.Id).UseIdentityByDefaultColumn();
        taxon.HasIndex(x => x.TaxonId).IsUnique();
        foreach (var property in new[]
        {
            nameof(WfoTaxon.ScientificName), nameof(WfoTaxon.TaxonRank),
            nameof(WfoTaxon.Family), nameof(WfoTaxon.Genus), nameof(WfoTaxon.SpecificEpithet),
            nameof(WfoTaxon.TaxonomicStatus), nameof(WfoTaxon.AcceptedNameUsageId),
            nameof(WfoTaxon.ParentNameUsageId), nameof(WfoTaxon.ScientificNameId),
            nameof(WfoTaxon.TplId), nameof(WfoTaxon.IsCurrent)
        }) taxon.HasIndex(property);
        taxon.HasOne<WfoImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);
        taxon.HasOne(x => x.Parent).WithMany().HasForeignKey(x => x.ParentId).OnDelete(DeleteBehavior.Restrict);
        taxon.HasOne(x => x.AcceptedTaxon).WithMany().HasForeignKey(x => x.AcceptedTaxonId).OnDelete(DeleteBehavior.Restrict);
        taxon.HasOne(x => x.OriginalTaxon).WithMany().HasForeignKey(x => x.OriginalTaxonId).OnDelete(DeleteBehavior.Restrict);
    }
}
