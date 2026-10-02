using Microsoft.EntityFrameworkCore;
using BDIMS.Models;

namespace BDIMS.Data
{
    public class ApplicationDbContext : DbContext
    {
        public ApplicationDbContext(DbContextOptions<ApplicationDbContext> options)
            : base(options)
        {
        }

        protected override void OnModelCreating(ModelBuilder modelBuilder)
        {
            base.OnModelCreating(modelBuilder);

            // Sign-in lookups resolve an account by its normalized email, and the admin
            // seeder relies on that key identifying exactly one row. A unique index
            // makes "the account exists" a database invariant rather than a convention,
            // so two application instances starting simultaneously cannot both insert
            // the seeded administrator.
            modelBuilder.Entity<UserAccount>()
                .HasIndex(u => u.NormalizedEmail)
                .IsUnique()
                .HasDatabaseName("IX_UserAccounts_NormalizedEmail");
        }

        // DbSets map your C# model classes to tables in MySQL
        public DbSet<ResidentModel> Residents { get; set; }
        public DbSet<AnnouncementModel> Announcements { get; set; }
        public DbSet<BlotterCaseModel> Blotters { get; set; }
        public DbSet<CertificateModel> Certificates { get; set; }
        public DbSet<DocumentRequestModel> DocumentRequests { get; set; }
        public DbSet<ReportModel> Reports { get; set; }
        public DbSet<CertificateRule> CertificateRules { get; set; }
        public DbSet<NotificationModel> Notifications { get; set; }
        public DbSet<AppSetting> AppSettings { get; set; }
        public DbSet<UserAccount> UserAccounts { get; set; }
    }
}