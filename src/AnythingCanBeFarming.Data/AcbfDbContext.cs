using Microsoft.EntityFrameworkCore;

namespace AnythingCanBeFarming.Data;

public sealed class AcbfDbContext(DbContextOptions<AcbfDbContext> options) : DbContext(options)
{
    public DbSet<WfoTaxon> WfoTaxa => Set<WfoTaxon>();
    public DbSet<WfoImport> WfoImports => Set<WfoImport>();
    public DbSet<WfoIpniMapping> WfoIpniMappings => Set<WfoIpniMapping>();
    public DbSet<WfoDeprecatedName> WfoDeprecatedNames => Set<WfoDeprecatedName>();
    public DbSet<WfoDeduplicatedId> WfoDeduplicatedIds => Set<WfoDeduplicatedId>();
    public DbSet<SourceImport> SourceImports => Set<SourceImport>();
    public DbSet<WikidataItem> WikidataItems => Set<WikidataItem>();
    public DbSet<WikidataWfoLink> WikidataWfoLinks => Set<WikidataWfoLink>();
    public DbSet<WikidataExternalId> WikidataExternalIds => Set<WikidataExternalId>();
    public DbSet<WikidataCommonName> WikidataCommonNames => Set<WikidataCommonName>();
    public DbSet<UsdaTaxon> UsdaTaxa => Set<UsdaTaxon>();
    public DbSet<UsdaFact> UsdaFacts => Set<UsdaFact>();
    public DbSet<UsdaRemark> UsdaRemarks => Set<UsdaRemark>();
    public DbSet<UsdaDistribution> UsdaDistributions => Set<UsdaDistribution>();
    public DbSet<UsdaWfoLink> UsdaWfoLinks => Set<UsdaWfoLink>();
    public DbSet<UsdaTraitType> UsdaTraitTypes => Set<UsdaTraitType>();
    public DbSet<UsdaCodeLabel> UsdaCodeLabels => Set<UsdaCodeLabel>();
    public DbSet<PlaceLabel> PlaceLabels => Set<PlaceLabel>();

    protected override void OnModelCreating(ModelBuilder modelBuilder)
    {
        modelBuilder.HasPostgresExtension("pg_trgm");

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
        taxon.HasIndex(x => x.ScientificName, "IX_wfo_taxon_ScientificName_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");
        taxon.HasIndex(x => x.Genus, "IX_wfo_taxon_Genus_trgm").HasMethod("gin").HasOperators("gin_trgm_ops");

        var sourceImports = modelBuilder.Entity<SourceImport>();
        sourceImports.ToTable("source_import", "reference");
        sourceImports.Property(x => x.Id).UseIdentityByDefaultColumn();
        sourceImports.Property(x => x.ParametersJson).HasColumnType("jsonb");
        sourceImports.Property(x => x.ValidationJson).HasColumnType("jsonb");
        sourceImports.HasIndex(x => new { x.Source, x.Kind, x.Status });

        var items = modelBuilder.Entity<WikidataItem>();
        items.ToTable("wikidata_item", "reference");
        items.Property(x => x.Id).UseIdentityByDefaultColumn();
        items.HasIndex(x => x.Qid).IsUnique();
        items.HasIndex(x => x.IsCurrent);
        items.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.CrosswalkImportId).OnDelete(DeleteBehavior.Restrict);
        items.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.DetailsImportId).OnDelete(DeleteBehavior.Restrict);

        var links = modelBuilder.Entity<WikidataWfoLink>();
        links.ToTable("wikidata_wfo_link", "reference");
        links.Property(x => x.Id).UseIdentityByDefaultColumn();
        links.HasIndex(x => new { x.Qid, x.WfoId }).IsUnique();
        links.HasIndex(x => x.WfoId);
        links.HasIndex(x => x.WfoTaxonId);
        links.HasIndex(x => x.AcceptedWfoTaxonId);
        links.HasOne<WikidataItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);
        links.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.WfoTaxonId).OnDelete(DeleteBehavior.Restrict);
        links.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.AcceptedWfoTaxonId).OnDelete(DeleteBehavior.Restrict);
        links.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var externalIds = modelBuilder.Entity<WikidataExternalId>();
        externalIds.ToTable("wikidata_external_id", "reference");
        externalIds.Property(x => x.Id).UseIdentityByDefaultColumn();
        externalIds.HasIndex(x => new { x.ItemId, x.Property, x.Value }).IsUnique();
        externalIds.HasIndex(x => new { x.Property, x.Value });
        externalIds.HasOne<WikidataItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);

        var commonNames = modelBuilder.Entity<WikidataCommonName>();
        commonNames.ToTable("wikidata_common_name", "reference");
        commonNames.Property(x => x.Id).UseIdentityByDefaultColumn();
        commonNames.HasIndex(x => new { x.ItemId, x.Language, x.Name }).IsUnique();
        // Keep this expression identical to NameNormalizer.Compact.
        commonNames.Property(x => x.CompactName)
            .HasComputedColumnSql("""replace(replace("NormalizedName", ' ', ''), '-', '')""", stored: true);
        commonNames.HasIndex(x => x.CompactName, "IX_wikidata_common_name_CompactName_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        commonNames.HasIndex(x => x.NormalizedName, "IX_wikidata_common_name_NormalizedName_trgm")
            .HasMethod("gin").HasOperators("gin_trgm_ops");
        commonNames.HasOne<WikidataItem>().WithMany().HasForeignKey(x => x.ItemId).OnDelete(DeleteBehavior.Restrict);

        var usdaTaxa = modelBuilder.Entity<UsdaTaxon>();
        usdaTaxa.ToTable("usda_taxon", "reference");
        usdaTaxa.Property(x => x.Id).UseIdentityByDefaultColumn();
        usdaTaxa.HasIndex(x => x.Symbol).IsUnique();
        usdaTaxa.HasIndex(x => x.CanonicalName);
        usdaTaxa.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var remarks = modelBuilder.Entity<UsdaRemark>();
        remarks.ToTable("usda_remark", "reference");
        remarks.Property(x => x.Id).UseIdentityByDefaultColumn();
        remarks.HasIndex(x => x.Sha256).IsUnique();

        var facts = modelBuilder.Entity<UsdaFact>();
        facts.ToTable("usda_fact", "reference");
        facts.Property(x => x.Id).UseIdentityByDefaultColumn();
        facts.Property(x => x.ValueNumeric).HasColumnType("numeric");
        facts.Property(x => x.ValueCode).HasComputedColumnSql(UsdaCode.ValueCodeSql, stored: true);
        facts.HasIndex(x => x.Symbol);
        facts.HasIndex(x => x.TypeUri);
        facts.HasIndex(x => new { x.TypeUri, x.ValueRaw });
        facts.HasOne<UsdaTaxon>().WithMany().HasForeignKey(x => x.UsdaTaxonId).OnDelete(DeleteBehavior.Restrict);
        facts.HasOne<UsdaRemark>().WithMany().HasForeignKey(x => x.RemarkId).OnDelete(DeleteBehavior.Restrict);
        facts.HasOne<UsdaRemark>().WithMany().HasForeignKey(x => x.MethodRemarkId).OnDelete(DeleteBehavior.Restrict);
        facts.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var distribution = modelBuilder.Entity<UsdaDistribution>();
        distribution.ToTable("usda_distribution", "reference");
        distribution.Property(x => x.Id).UseIdentityByDefaultColumn();
        distribution.HasIndex(x => new { x.UsdaTaxonId, x.Kind, x.PlaceRaw }).IsUnique();
        distribution.HasIndex(x => new { x.Kind, x.PlaceId });
        distribution.HasIndex(x => x.Symbol);
        distribution.HasOne<UsdaTaxon>().WithMany().HasForeignKey(x => x.UsdaTaxonId).OnDelete(DeleteBehavior.Restrict);
        distribution.HasOne<UsdaRemark>().WithMany().HasForeignKey(x => x.RemarkId).OnDelete(DeleteBehavior.Restrict);
        distribution.HasOne<SourceImport>().WithMany().HasForeignKey(x => x.ImportId).OnDelete(DeleteBehavior.Restrict);

        var usdaLinks = modelBuilder.Entity<UsdaWfoLink>();
        usdaLinks.ToTable("usda_wfo_link", "reference");
        usdaLinks.Property(x => x.Id).UseIdentityByDefaultColumn();
        usdaLinks.Property(x => x.DetailJson).HasColumnType("jsonb");
        usdaLinks.HasIndex(x => x.UsdaTaxonId).IsUnique();
        usdaLinks.HasIndex(x => x.Symbol);
        usdaLinks.HasIndex(x => x.WfoTaxonId);
        usdaLinks.HasIndex(x => x.AcceptedWfoTaxonId);
        usdaLinks.HasOne<UsdaTaxon>().WithMany().HasForeignKey(x => x.UsdaTaxonId).OnDelete(DeleteBehavior.Restrict);
        usdaLinks.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.WfoTaxonId).OnDelete(DeleteBehavior.Restrict);
        usdaLinks.HasOne<WfoTaxon>().WithMany().HasForeignKey(x => x.AcceptedWfoTaxonId).OnDelete(DeleteBehavior.Restrict);

        var traitTypes = modelBuilder.Entity<UsdaTraitType>();
        traitTypes.ToTable("usda_trait_type", "reference");
        traitTypes.HasKey(x => x.TypeUri);
        traitTypes.HasIndex(x => x.Key).IsUnique();

        var codeLabels = modelBuilder.Entity<UsdaCodeLabel>();
        codeLabels.ToTable("usda_code_label", "reference");
        codeLabels.Property(x => x.Id).UseIdentityByDefaultColumn();
        // A generic row (TypeUri null) and a trait-specific override may share a code, but neither may repeat.
        codeLabels.HasIndex(x => new { x.Code, x.TypeUri }).IsUnique().AreNullsDistinct(false);

        var places = modelBuilder.Entity<PlaceLabel>();
        places.ToTable("place_label", "reference");
        places.HasKey(x => new { x.Scheme, x.PlaceId });
        places.HasIndex(x => new { x.CountryCode, x.AdminCode });
    }
}
