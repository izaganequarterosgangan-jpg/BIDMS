using System;
using System.Threading;
using System.Threading.Tasks;
using BDIMS.Data;
using BDIMS.Models;
using Microsoft.AspNetCore.Identity;
using Microsoft.EntityFrameworkCore;
using Microsoft.Extensions.Logging;

namespace BDIMS.Services
{
    /// <summary>
    /// Creates and reconciles the BDIMS sign-in accounts on start-up.
    ///
    /// This runs after <c>Database.Migrate()</c> (see <c>Program.cs</c>), so the
    /// <c>UserAccounts</c> table is guaranteed to exist before it is queried.
    ///
    /// The seeding is idempotent by design: it is executed on every application
    /// start, and it never creates a second row for an account that already exists.
    /// </summary>
    public static class DbInitializer
    {
        /// <summary>
        /// Login identifier of the initial administrator. This is the lookup key for
        /// the whole routine - an account is considered "the admin account" if and
        /// only if its normalized email matches this value.
        /// </summary>
        public const string AdminEmail = "izaganequarterosgangan@gmail.com";

        /// <summary>Full name rendered in the sidebar user widget.</summary>
        public const string AdminDisplayName = "Isagane Quarteros";

        /// <summary>Job title rendered under the display name.</summary>
        public const string AdminRole = "Barangay Secretary";

        /// <summary>
        /// Bootstrap password. It is only ever applied when the account is first
        /// created; an existing account keeps whatever hash it already has, so this
        /// value cannot silently reset a password that the operator has since changed.
        /// </summary>
        private const string AdminInitialPassword = "Password";

        /// <summary>
        /// Ensures the initial administrator account exists and is consistent with the
        /// values above, then returns it.
        /// </summary>
        /// <remarks>
        /// Safe to call on every start-up: it performs at most one write, and only when
        /// the stored row actually differs from the expected profile.
        /// </remarks>
        public static async Task<UserAccount> SeedAdminAccountAsync(
            ApplicationDbContext db,
            ILogger? logger = null,
            CancellationToken cancellationToken = default)
        {
            if (db == null) throw new ArgumentNullException(nameof(db));

            var normalizedEmail = UserAccount.NormalizeEmail(AdminEmail);
            var expectedInitials = UserAccount.DeriveInitials(AdminDisplayName);

            // Single tracked query, so the update below reuses this same instance
            // instead of attaching a duplicate copy of the row.
            var admin = await db.UserAccounts
                .SingleOrDefaultAsync(u => u.NormalizedEmail == normalizedEmail, cancellationToken)
                .ConfigureAwait(false);

            if (admin == null)
            {
                admin = new UserAccount
                {
                    Email = AdminEmail.Trim(),
                    NormalizedEmail = normalizedEmail,
                    // PasswordHasher generates a fresh random salt and PBKDF2 iteration
                    // count per call, so this hash is unique and the plaintext above is
                    // never persisted anywhere.
                    PasswordHash = new PasswordHasher<UserAccount>().HashPassword(null!, AdminInitialPassword),
                    DisplayName = AdminDisplayName,
                    Role = AdminRole,
                    Initials = expectedInitials,
                    IsAdmin = true,
                    IsActive = true,
                    CreatedUtc = DateTime.UtcNow
                };

                db.UserAccounts.Add(admin);
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                logger?.LogInformation(
                    "Seeded initial BDIMS admin account {Email} (role {Role}).",
                    AdminEmail,
                    AdminRole);

                return admin;
            }

            return await ReconcileAsync(db, admin, expectedInitials, logger, cancellationToken)
                .ConfigureAwait(false);
        }


        /// <summary>
        /// Brings an existing admin row back in line with the expected profile without
        /// touching its credentials or key.
        /// </summary>
        private static async Task<UserAccount> ReconcileAsync(
            ApplicationDbContext db,
            UserAccount admin,
            string expectedInitials,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            // PasswordHash, CreatedUtc and Id are deliberately left alone: re-hashing
            // here would invalidate the operator's current password on every start-up,
            // and regenerating the key would orphan anything referencing this account.
            var profileChanged = false;

            if (!string.Equals(admin.DisplayName, AdminDisplayName, StringComparison.Ordinal))
            {
                admin.DisplayName = AdminDisplayName;
                profileChanged = true;
            }

            if (!string.Equals(admin.Role, AdminRole, StringComparison.Ordinal))
            {
                admin.Role = AdminRole;
                profileChanged = true;
            }

            if (!string.Equals(admin.Initials, expectedInitials, StringComparison.Ordinal))
            {
                admin.Initials = expectedInitials;
                profileChanged = true;
            }

            // A deactivated or demoted admin would lock the deployment out of its own
            // modules, so the seed re-asserts full access rather than trusting the row.
            if (!admin.IsAdmin)
            {
                admin.IsAdmin = true;
                profileChanged = true;
            }

            if (!admin.IsActive)
            {
                admin.IsActive = true;
                profileChanged = true;
            }

            if (profileChanged)
            {
                await db.SaveChangesAsync(cancellationToken).ConfigureAwait(false);

                logger?.LogInformation(
                    "Reconciled existing BDIMS admin account {Email} with the expected profile; " +
                    "the existing password hash was preserved.",
                    AdminEmail);
            }

            return admin;
        }
    }
}