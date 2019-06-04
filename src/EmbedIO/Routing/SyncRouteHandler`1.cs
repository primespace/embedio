﻿using System.Threading;

namespace EmbedIO.Routing
{
    /// <summary>
    /// Base class for callbacks used to handle routed requests synchronously.
    /// </summary>
    /// <typeparam name="TContext">The type of the context.</typeparam>
    /// <param name="context">A <see cref="IHttpContext" /> interface representing the context of the request.</param>
    /// <param name="route">The matched route.</param>
    /// <param name="cancellationToken">A <see cref="CancellationToken" /> used to cancel the operation.</param>
    /// <returns><see langword="true"/>if the request has been handled; otherwise, <see langword="false"/>.</returns>
    /// <seealso cref="RouteMatch"/>
    public delegate bool SyncRouteHandler<in TContext>(TContext context, RouteMatch route, CancellationToken cancellationToken);
}