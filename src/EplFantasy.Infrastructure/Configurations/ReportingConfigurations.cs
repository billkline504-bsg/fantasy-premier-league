using EplFantasy.Reporting;
using Microsoft.EntityFrameworkCore;
using Microsoft.EntityFrameworkCore.Metadata.Builders;

namespace EplFantasy.Infrastructure.Configurations;

public class PlayerSeasonStatisticsConfiguration : IEntityTypeConfiguration<PlayerSeasonStatistics>
{
    public void Configure(EntityTypeBuilder<PlayerSeasonStatistics> builder)
    {
        // Recomputed read-model, never a directly persisted/updatable table (Architecture §6.6) —
        // mapped to the player_season_statistics SQL view (V008) as a keyless entity.
        builder.HasNoKey();
        builder.ToView("player_season_statistics");
    }
}
