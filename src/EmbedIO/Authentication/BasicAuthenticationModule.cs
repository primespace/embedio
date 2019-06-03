﻿using System;
using System.Collections.Concurrent;
using System.Threading;
using System.Threading.Tasks;

namespace EmbedIO.Authentication
{
    /// <summary>
    /// Simple HTTP basic authorization module that stores credentials
    /// in a <seealso cref="ConcurrentDictionary{TKey,TValue}"/>.
    /// </summary>
    public class BasicAuthenticationModule : BasicAuthenticationModuleBase
    {
        /// <summary>
        /// Initializes a new instance of the <see cref="BasicAuthenticationModule"/> class.
        /// </summary>
        /// <param name="baseUrlPath">The base URL path.</param>
        /// <param name="realm">The authentication realm.</param>
        /// <remarks>
        /// <para>If <paramref name="realm"/> is <see langword="null"/> or the empty string,
        /// the <see cref="Realm"/> property will be set equal to
        /// <see cref="IWebModule.BaseUrlPath">BaseUrlPath</see>.</para>
        /// </remarks>
        public BasicAuthenticationModule(string baseUrlPath, string realm = null)
            : base(baseUrlPath, realm)
        {
        }

        /// <summary>
        /// Gets a dictionary of valid user names and passwords.
        /// </summary>
        /// <value>
        /// The accounts.
        /// </value>
        public ConcurrentDictionary<string, string> Accounts { get; } = new ConcurrentDictionary<string, string>(StringComparer.InvariantCulture);

        /// <inheritdoc />
        protected override Task<bool> VerifyCredentialsAsync(string path, string userName, string password, CancellationToken cancellationToken)
            => Task.FromResult(VerifyCredentialsInternal(userName, password));

        private bool VerifyCredentialsInternal(string userName, string password)
            => userName != null
            && Accounts.TryGetValue(userName, out var storedPassword)
            && string.Equals(password, storedPassword, StringComparison.Ordinal);
    }
}
