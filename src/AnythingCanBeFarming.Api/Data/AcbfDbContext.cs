using Microsoft.EntityFrameworkCore;

namespace AnythingCanBeFarming.Api.Data;

// Infrastructure only. No entities, schema creation, or migrations yet.
public sealed class AcbfDbContext(DbContextOptions<AcbfDbContext> options) : DbContext(options);
