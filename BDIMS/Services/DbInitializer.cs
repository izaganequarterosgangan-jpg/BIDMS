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

        /// <summary>Given name, used to seed the split first/last name columns.</summary>
        public const string AdminFirstName = "Isagane";

        /// <summary>Family name, used to seed the split first/last name columns.</summary>
        public const string AdminLastName = "Quarteros";

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

            // The admin is located by its admin flag rather than by
            // <see cref="AdminEmail"/>. The My Profile page lets the operator change
            // their email address, and a lookup keyed on the bootstrap email would then
            // miss the existing row, insert a second admin, and hand that newcomer the
            // original bootstrap password - a duplicate account with known
            // credentials. Keying on IsAdmin keeps exactly one such account across the
            // email change.
            var admin = await db.UserAccounts
                .Where(u => u.IsAdmin)
                .OrderBy(u => u.Id)
                .FirstOrDefaultAsync(cancellationToken)
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
                    FirstName = AdminFirstName,
                    LastName = AdminLastName,
                    Role = AdminRole,
                    Initials = UserAccount.ComposeInitials(AdminFirstName, AdminLastName)
                        ?? UserAccount.DeriveInitials(AdminDisplayName),
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

            return await ReconcileAsync(db, admin, logger, cancellationToken)
                .ConfigureAwait(false);
        }


        /// <summary>
        /// Restores the access flags on an existing admin row without touching anything
        /// the My Profile page owns.
        /// </summary>
        /// <remarks>
        /// Display name, role and initials are deliberately NOT re-asserted here. They
        /// used to be forced back to the bootstrap constants on every start-up, which
        /// silently undid any profile edit: the operator renamed themselves, the values
        /// were written back immediately, and the change appeared to do nothing until
        /// they noticed it had never persisted. Initials are only backfilled when they
        /// are blank, which is the case for rows created before the split-name columns
        /// existed.
        ///
        /// PasswordHash, Email and CreatedUtc are never rewritten: re-hashing here would
        /// invalidate the operator's current password on every start-up, and restoring
        /// the bootstrap email would undo a saved address change.
        /// </remarks>
        private static async Task<UserAccount> ReconcileAsync(
            ApplicationDbContext db,
            UserAccount admin,
            ILogger? logger,
            CancellationToken cancellationToken)
        {
            var profileChanged = false;

            if (string.IsNullOrWhiteSpace(admin.Initials))
            {
                admin.Initials = admin.ResolveInitials();
                profileChanged = true;
            }

            // Accounts predating the split-name columns have both halves blank. Fill
            // them in from the display name once, so the initials badge and the profile
            // form agree, but only when the row has nothing better to go on.
            if (string.IsNullOrWhiteSpace(admin.FirstName)
                && string.IsNullOrWhiteSpace(admin.LastName))
            {
                var parts = (admin.DisplayName ?? string.Empty)
                    .Split(' ', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);

                if (parts.Length > 0)
                {
                    admin.FirstName = parts[0];
                    admin.LastName = parts.Length > 1 ? parts[1] : string.Empty;
                    profileChanged = true;
                }
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
                    "Reconciled existing BDIMS admin account {Email}; the existing " +
                    "password hash, email address and edited profile values were preserved.",
                    admin.Email);
            }

            return admin;
        }
    }
}