namespace BDIMS.Models
{
    /// <summary>
    /// Registration/access lifecycle of a <see cref="UserAccount"/>.
    ///
    /// Stored as the enum name (a short string) rather than the underlying integer so
    /// the value readable in the database matches the value shown in the UI and in
    /// support queries - see the conversion configured in
    /// <c>ApplicationDbContext.OnModelCreating</c>.
    ///
    /// Only <see cref="Active"/> accounts may sign in; every other state is refused at
    /// the login form with a message explaining why.
    /// </summary>
    public enum UserAccountStatus
    {
        /// <summary>Registered but not yet vetted by an administrator. Cannot sign in.</summary>
        Pending,

        /// <summary>Approved and in good standing. The only state that can sign in.</summary>
        Active,

        /// <summary>Refused by an administrator. Cannot sign in until re-approved.</summary>
        Rejected,

        /// <summary>
        /// Temporarily locked by an administrator for a fixed number of hours. The
        /// expiry itself lives in <see cref="UserAccount.LockoutEnd"/>, so the block
        /// lifts on its own and <see cref="Active"/> is restored by the code that
        /// finds the elapsed lock (login, and the user list). See
        /// <see cref="UserAccount.HasExpiredLock"/>.
        /// </summary>
        Blocked,

        /// <summary>
        /// Switched off by an administrator without being rejected or deleted. Cannot
        /// sign in until it is toggled back to <see cref="Active"/>.
        /// </summary>
        Deactivated
    }
}
