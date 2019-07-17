﻿using System;
using System.Collections.Generic;
using System.Linq;
using System.Linq.Expressions;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using EmbedIO.Internal;
using EmbedIO.Routing;
using EmbedIO.Utilities;

namespace EmbedIO.WebApi
{
    /// <summary>
    /// <para>A module using class methods as handlers.</para>
    /// <para>Public instance methods that match the WebServerModule.ResponseHandler signature, and have the WebApi handler attribute
    /// will be used to respond to web server requests.</para>
    /// </summary>
    public abstract class WebApiModuleBase : RoutingModuleBase
    {
        private const string GetRequestDataAsyncMethodName = nameof(IRequestDataAttribute<WebApiController>.GetRequestDataAsync);

        private static readonly MethodInfo TaskFromResultBoolMethod = typeof(Task).GetMethod(nameof(Task.FromResult)).MakeGenericMethod(typeof(bool));
        private static readonly MethodInfo PreProcessRequestMethod = typeof(WebApiController).GetMethod(nameof(WebApiController.PreProcessRequest));
        private static readonly MethodInfo HttpContextSetter = typeof(WebApiController).GetProperty(nameof(WebApiController.HttpContext)).GetSetMethod(true);
        private static readonly MethodInfo RouteSetter = typeof(WebApiController).GetProperty(nameof(WebApiController.Route)).GetSetMethod(true);
        private static readonly MethodInfo CancellationTokenSetter = typeof(WebApiController).GetProperty(nameof(WebApiController.CancellationToken)).GetSetMethod(true);
        private static readonly MethodInfo AwaitResultMethod = typeof(WebApiModuleBase).GetMethod(nameof(AwaitResult), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo AwaitAndCastResultMethod = typeof(WebApiModuleBase).GetMethod(nameof(AwaitAndCastResult), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo TaskToBoolTaskMethod = typeof(WebApiModuleBase).GetMethod(nameof(TaskToBoolTask), BindingFlags.Static | BindingFlags.NonPublic);
        private static readonly MethodInfo DisposeMethod = typeof(IDisposable).GetMethod(nameof(IDisposable.Dispose));

        private readonly MethodInfo _serializeAsyncControllerResultAsyncMethod;
        private readonly MethodInfo _serializeNonAsyncControllerResultAsyncMethod;

        private readonly HashSet<Type> _controllerTypes = new HashSet<Type>();

        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiModuleBase" /> class,
        /// using the default response serializer.
        /// </summary>
        /// <param name="baseUrlPath">The base URL path served by this module.</param>
        /// <seealso cref="IWebModule.BaseUrlPath" />
        /// <seealso cref="Validate.UrlPath" />
        protected WebApiModuleBase(string baseUrlPath)
            : this(baseUrlPath, ResponseSerializer.Default)
        {
        }

        /// <summary>
        /// Initializes a new instance of the <see cref="WebApiModuleBase" /> class,
        /// using the specified response serializer.
        /// </summary>
        /// <param name="baseUrlPath">The base URL path served by this module.</param>
        /// <param name="serializer">A <see cref="ResponseSerializerCallback"/> used to serialize
        /// the result of controller methods returning <see langword="object"/>
        /// or <see cref="Task{TResult}">Task&lt;object&gt;</see>.</param>
        /// <exception cref="ArgumentNullException"><paramref name="serializer"/> is <see langword="null"/>.</exception>
        /// <seealso cref="IWebModule.BaseUrlPath" />
        /// <seealso cref="Validate.UrlPath" />
        protected WebApiModuleBase(string baseUrlPath, ResponseSerializerCallback serializer)
            : base(baseUrlPath)
        {
            Serializer = Validate.NotNull(nameof(serializer), serializer);

            _serializeAsyncControllerResultAsyncMethod = new Func<IHttpContext, Task<object>, CancellationToken, Task<bool>>(SerializeAsyncControllerResultAsync).Method;
            _serializeNonAsyncControllerResultAsyncMethod = new Func<IHttpContext, object, CancellationToken, Task<bool>>(SerializeNonAsyncControllerResultAsync).Method;
        }

        /// <summary>
        /// A <see cref="ResponseSerializerCallback"/> used to serialize
        /// the result of controller methods returning <see langword="object"/>
        /// or <see cref="Task{TResult}">Task&lt;object&gt;</see>.
        /// </summary>
        public ResponseSerializerCallback Serializer { get; }

        /// <summary>
        /// Gets the number of controller types registered in this module.
        /// </summary>
        public int ControllerCount => _controllerTypes.Count;

        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>In order for registration to be successful, the specified controller type:</para>
        /// <list type="bullet">
        /// <item><description>must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description>must not be an abstract class;</description></item>
        /// <item><description>must not be a generic type definition;</description></item>
        /// <item><description>must have a public parameterless constructor.</description></item>
        /// </list>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para><typeparamref name="TController"/> does not satisfy the prerequisites
        /// listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para>A new instance of <typeparamref name="TController"/> will be created
        /// for each request to handle, and dereferenced immediately afterwards,
        /// to be collected during next garbage collection cycle.</para>
        /// <para><typeparamref name="TController"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <typeparamref name="TController"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType{TController}(Func{TController})"/>
        /// <seealso cref="RegisterControllerType(Type)"/>
        protected void RegisterControllerType<TController>()
            where TController : WebApiController, new()
            => RegisterControllerType(typeof(TController));

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>In order for registration to be successful:</para>
        /// <list type="bullet">
        /// <item><description><typeparamref name="TController"/> must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description><typeparamref name="TController"/> must not be a generic type definition;</description></item>
        /// <item><description><paramref name="factory"/>'s return type must be either <typeparamref name="TController"/>
        /// or a subclass of <typeparamref name="TController"/>.</description></item>
        /// </list>
        /// </summary>
        /// <typeparam name="TController">The type of the controller.</typeparam>
        /// <param name="factory">The factory method used to construct instances of <typeparamref name="TController"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="factory"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><typeparamref name="TController"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="factory"/> does not satisfy the prerequisites listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para><paramref name="factory"/>will be called once for each request to handle
        /// in order to obtain an instance of <typeparamref name="TController"/>.
        /// The returned instance will be dereferenced immediately after handling the request.</para>
        /// <para><typeparamref name="TController"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <typeparamref name="TController"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request. In this case it is recommended that
        /// <paramref name="factory"/> return a newly-constructed instance of <typeparamref name="TController"/>
        /// at each invocation.</para>
        /// <para>If <typeparamref name="TController"/> does not implement <see cref="IDisposable"/>,
        /// <paramref name="factory"/> may employ techniques such as instance pooling to avoid
        /// the overhead of constructing a new instance of <typeparamref name="TController"/>
        /// at each invocation. If so, resources such as file handles, database connections, etc.
        /// should be freed before returning from each handler method to avoid
        /// <see href="https://en.wikipedia.org/wiki/Starvation_(computer_science)">starvation</see>.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType{TController}()"/>
        /// <seealso cref="RegisterControllerType(Type,Func{WebApiController})"/>
        protected void RegisterControllerType<TController>(Func<TController> factory)
            where TController : WebApiController
            => RegisterControllerType(typeof(TController), factory);

        /// <summary>
        /// <para>Registers a controller type using a constructor.</para>
        /// <para>In order for registration to be successful, the specified <paramref name="controllerType"/>: </para>
        /// <list type="bullet">
        /// <item><description>must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description>must not be an abstract class;</description></item>
        /// <item><description>must not be a generic type definition;</description></item>
        /// <item><description>must have a public parameterless constructor.</description></item>
        /// </list>
        /// </summary>
        /// <param name="controllerType">The type of the controller.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException"><paramref name="controllerType"/> is <see langword="null"/>.</exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="controllerType"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para><paramref name="controllerType"/> does not satisfy the prerequisites
        /// listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para>A new instance of <paramref name="controllerType"/> will be created
        /// for each request to handle, and dereferenced immediately afterwards,
        /// to be collected during next garbage collection cycle.</para>
        /// <para><paramref name="controllerType"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <paramref name="controllerType"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType(Type,Func{WebApiController})"/>
        /// <seealso cref="RegisterControllerType{TController}()"/>
        protected void RegisterControllerType(Type controllerType)
        {
            EnsureConfigurationNotLocked();

            controllerType = ValidateControllerType(nameof(controllerType), controllerType, false);

            var constructor = controllerType.GetConstructors().FirstOrDefault(c => c.GetParameters().Length == 0);
            if (constructor == null)
            {
                throw new ArgumentException(
                    "Controller type must have a public parameterless constructor.",
                    nameof(controllerType));
            }

            RegisterControllerTypeCore(controllerType, Expression.New(constructor));
        }

        /// <summary>
        /// <para>Registers a controller type using a factory method.</para>
        /// <para>In order for registration to be successful:</para>
        /// <list type="bullet">
        /// <item><description><paramref name="controllerType"/> must be a subclass of <see cref="WebApiController"/>;</description></item>
        /// <item><description><paramref name="controllerType"/> must not be a generic type definition;</description></item>
        /// <item><description><paramref name="factory"/>'s return type must be either <paramref name="controllerType"/>
        /// or a subclass of <paramref name="controllerType"/>.</description></item>
        /// </list>
        /// </summary>
        /// <param name="controllerType">The type of the controller.</param>
        /// <param name="factory">The factory method used to construct instances of <paramref name="controllerType"/>.</param>
        /// <exception cref="InvalidOperationException">The module's configuration is locked.</exception>
        /// <exception cref="ArgumentNullException">
        /// <para><paramref name="controllerType"/> is <see langword="null"/>.</para>
        /// <para>- or -</para>
        /// <para><paramref name="factory"/> is <see langword="null"/>.</para>
        /// </exception>
        /// <exception cref="ArgumentException">
        /// <para><paramref name="controllerType"/> is already registered in this module.</para>
        /// <para>- or -</para>
        /// <para>One or more parameters do not satisfy the prerequisites listed in the Summary section.</para>
        /// </exception>
        /// <remarks>
        /// <para><paramref name="factory"/>will be called once for each request to handle
        /// in order to obtain an instance of <paramref name="controllerType"/>.
        /// The returned instance will be dereferenced immediately after handling the request.</para>
        /// <para><paramref name="controllerType"/> is not required to be thread-safe,
        /// as it will be constructed and used in the same synchronization context.
        /// However, since request handling is asynchronous, the actual execution thread
        /// may vary during execution. Care must be exercised when using thread-sensitive
        /// resources or thread-static data.</para>
        /// <para>If <paramref name="controllerType"/> implements <see cref="IDisposable"/>,
        /// its <see cref="IDisposable.Dispose">Dispose</see> method will be called when it has
        /// finished handling a request. In this case it is recommended that
        /// <paramref name="factory"/> return a newly-constructed instance of <paramref name="controllerType"/>
        /// at each invocation.</para>
        /// <para>If <paramref name="controllerType"/> does not implement <see cref="IDisposable"/>,
        /// <paramref name="factory"/> may employ techniques such as instance pooling to avoid
        /// the overhead of constructing a new instance of <paramref name="controllerType"/>
        /// at each invocation. If so, resources such as file handles, database connections, etc.
        /// should be freed before returning from each handler method to avoid
        /// <see href="https://en.wikipedia.org/wiki/Starvation_(computer_science)">starvation</see>.</para>
        /// </remarks>
        /// <seealso cref="RegisterControllerType(Type)"/>
        /// <seealso cref="RegisterControllerType{TController}(Func{TController})"/>
        protected void RegisterControllerType(Type controllerType, Func<WebApiController> factory)
        {
            EnsureConfigurationNotLocked();

            controllerType = ValidateControllerType(nameof(controllerType), controllerType, true);
            factory = Validate.NotNull(nameof(factory), factory);
            if (!controllerType.IsAssignableFrom(factory.Method.ReturnType))
                throw new ArgumentException("Factory method has an incorrect return type.", nameof(factory));

            RegisterControllerTypeCore(controllerType, Expression.Call(
                factory.Target == null ? null : Expression.Constant(factory.Target),
                factory.Method));
        }

        private static int IndexOfRouteParameter(RouteMatcher matcher, string name)
        {
            var names = matcher.ParameterNames;
            for (var i = 0; i < names.Count; i++)
            {
                if (names[i] == name)
                    return i;
            }

            return -1;
        }

        // Compile a handler.
        //
        // Parameters:
        // - factoryExpression is an Expression that builds a controller;
        // - method is a MethodInfo for a public instance method of the controller
        //   returning either Task<bool>, bool, Task<object>, or object;
        // - route is the route to which the controller method is associated.
        //
        // This method builds a lambda, with the same signature as a RouteHandler<IHttpContext>, that:
        // - uses factoryExpression to build a controller;
        // - calls the controller method, passing converted route parameters for method parameters with matching names
        //   and default values for other parameters;
        // - according to the return type of the controller method:
        //   - if Task<bool>   - returns the result of the call;
        //   - if bool         - returns Task.FromResult<bool>(result);
        //   - if Task<object> - returns SerializeAsyncControllerResultAsync(context, result, cancellationToken);
        //   - if object       - returns SerializeNonAsyncControllerResultAsync(context, result, cancellationToken);
        //   - if Task         - returns TaskToBoolTask(result);
        //   - if void         - returns Task.FromResult(true);
        // - if the controller implements IDisposable, disposes it.
        private RouteHandler<IHttpContext> CompileHandler(Expression factoryExpression, MethodInfo method, string route)
        {
            // Parse the route
            var matcher = RouteMatcher.Parse(route);

            // Lambda parameters
            var contextInLambda = Expression.Parameter(typeof(IHttpContext), "context");
            var routeInLambda = Expression.Parameter(typeof(RouteMatch), "route");
            var cancellationTokenInLambda = Expression.Parameter(typeof(CancellationToken), "cancellationToken");

            // Local variables
            var locals = new List<ParameterExpression>();

            // Local variable for controller
            var controllerType = method.ReflectedType;
            var controller = Expression.Variable(controllerType, "controller");
            locals.Add(controller);

            // Label for return statement
            var returnTarget = Expression.Label(typeof(Task<bool>));

            // Contents of lambda body
            var bodyContents = new List<Expression>();

            // Build lambda arguments
            var parameters = method.GetParameters();
            var parameterCount = parameters.Length;
            var handlerArguments = new List<Expression>();
            for (var i = 0; i < parameterCount; i++)
            {
                var parameter = parameters[i];
                var parameterType = parameter.ParameterType;

                // First, check for generic request data interfaces in attributes
                var requestDataInterfaces = parameter.GetCustomAttributes<Attribute>()
                        .Aggregate(new List<(Attribute Attr, Type Intf)>(), (list, attr) => {
                            list.AddRange(attr.GetType().GetInterfaces()
                                .Where(x => x.IsConstructedGenericType
                                         && x.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<,>))
                                .Select(x => (attr, x)));

                            return list;
                        });

                // If there are any...
                if (requestDataInterfaces.Count > 0)
                {
                    // Take the first that applies to both controller and parameter type
                    var (attr, intf) = requestDataInterfaces.FirstOrDefault(
                        x => x.Intf.GenericTypeArguments[0].IsAssignableFrom(controllerType)
                          && parameterType.IsAssignableFrom(x.Intf.GenericTypeArguments[1]));

                    // Throw if there are none, as the user expects data to be injected
                    // but provided no way of injecting the right data type.
                    if (attr == null)
                        throw new InvalidOperationException($"No request data attribute for parameter {parameter.Name} of method {controllerType.Name}.{method.Name} can provide the expected data type.");

                    // Use the request data interface to get a value for the parameter.
                    Expression useRequestDataInterface = Expression.Call(
                        Expression.Constant(attr),
                        intf.GetMethod(GetRequestDataAsyncMethodName),
                        controller,
                        Expression.Constant(parameter.Name));

                    // We should await the call to GetRequestDataAsync.
                    // For lack of a better way, call AwaitResult with an appropriate type argument.
                    useRequestDataInterface = Expression.Call(
                        AwaitResultMethod.MakeGenericMethod(intf.GenericTypeArguments[1]),
                        useRequestDataInterface);

                    handlerArguments.Add(useRequestDataInterface);
                    continue;
                }

                // Check for non-generic request data interfaces in attributes
                requestDataInterfaces = parameter.GetCustomAttributes<Attribute>()
                        .Aggregate(new List<(Attribute Attr, Type Intf)>(), (list, attr) => {
                            list.AddRange(attr.GetType().GetInterfaces()
                                .Where(x => x.IsConstructedGenericType
                                         && x.GetGenericTypeDefinition() == typeof(IRequestDataAttribute<>))
                                .Select(x => (attr, x)));

                            return list;
                        });

                // If there are any...
                if (requestDataInterfaces.Count > 0)
                {
                    // Take the first that applies to the controller
                    var (attr, intf) = requestDataInterfaces.FirstOrDefault(
                        x => x.Intf.GenericTypeArguments[0].IsAssignableFrom(controllerType));

                    // Throw if there are none, as the user expects data to be injected
                    // but provided no way of injecting the right data type.
                    if (attr == null)
                        throw new InvalidOperationException($"No request data attribute for parameter {parameter.Name} of method {controllerType.Name}.{method.Name} can provide the expected data type.");

                    // Use the request data interface to get a value for the parameter.
                    Expression useRequestDataInterface = Expression.Call(
                        Expression.Constant(attr),
                        intf.GetMethod(GetRequestDataAsyncMethodName),
                        controller,
                        Expression.Constant(parameterType),
                        Expression.Constant(parameter.Name));

                    // We should await the call to GetRequestDataAsync,
                    // then cast the result to the parameter type.
                    // For lack of a better way to do the former,
                    // and to save one function call,
                    // just call AwaitAndCastResult with an appropriate type argument.
                    useRequestDataInterface = Expression.Call(
                        AwaitAndCastResultMethod.MakeGenericMethod(parameterType),
                        Expression.Constant(parameter.Name),
                        useRequestDataInterface);

                    handlerArguments.Add(useRequestDataInterface);
                    continue;
                }

                // Check whether the name of the handler parameter matches the name of a route parameter.
                var index = IndexOfRouteParameter(matcher, parameter.Name);
                if (index >= 0)
                {
                    // Convert the parameter to the handler's parameter type.
                    var convertFromRoute = FromString.ConvertExpressionTo(
                        parameterType,
                        Expression.Property(routeInLambda, "Item", Expression.Constant(index)));

                    handlerArguments.Add(convertFromRoute);
                    continue;
                }

                // No route parameter has the same name as a handler parameter.
                // Pass the default for the parameter type.
                handlerArguments.Add(Expression.Constant(parameter.HasDefaultValue
                    ? parameter.DefaultValue
                        : parameterType.IsValueType
                        ? Activator.CreateInstance(parameterType)
                        : null));
            }

            // Create the controller and initialize its properties
            bodyContents.Add(Expression.Assign(controller,factoryExpression));
            bodyContents.Add(Expression.Call(controller, HttpContextSetter, contextInLambda));
            bodyContents.Add(Expression.Call(controller, RouteSetter, routeInLambda));
            bodyContents.Add(Expression.Call(controller, CancellationTokenSetter, cancellationTokenInLambda));

            // Build the handler method call
            Expression callMethod = Expression.Call(controller, method, handlerArguments);
            var methodReturnType = method.ReturnType;
            if (methodReturnType == typeof(Task<bool>))
            {
                // Nothing to do
            }
            else if (methodReturnType == typeof(bool))
            {
                // Convert bool to Task<bool>
                callMethod = Expression.Call(TaskFromResultBoolMethod, callMethod);
            }
            else if (methodReturnType == typeof(Task<object>))
            {
                // Serialize result of Task<object> and return true
                callMethod = Expression.Call(
                    Expression.Constant(this),
                    _serializeAsyncControllerResultAsyncMethod,
                    contextInLambda,
                    callMethod,
                    cancellationTokenInLambda);
            }
            else if (methodReturnType == typeof(object))
            {
                // Serialize object and return true
                callMethod = Expression.Call(
                    Expression.Constant(this),
                    _serializeNonAsyncControllerResultAsyncMethod,
                    contextInLambda,
                    callMethod,
                    cancellationTokenInLambda);
            }
            else if (methodReturnType == typeof(Task))
            {
                // Await task and return true
                callMethod = Expression.Call(Expression.Constant(this), TaskToBoolTaskMethod, callMethod);
            }
            else if (methodReturnType == typeof(void))
            {
                // Call method and return true
                callMethod = Expression.Block(
                    callMethod,
                    Expression.Call(TaskFromResultBoolMethod, Expression.Constant(true)));
            }
            else
            {
                // This is an internal error, as the return type should have been checked earlier.
                SelfCheck.Fail($"Controller method has unexpected return type {methodReturnType.FullName}");
            }

            // Operations to perform on the controller.
            // Pseudocode:
            //     controller.PreProcessRequest();
            //     return controller.method(handlerArguments);
            Expression workWithController = Expression.Block(
                Expression.Call(controller, PreProcessRequestMethod),
                Expression.Return(returnTarget, callMethod));

            // If the controller type implements IDisposable,
            // wrap operations in a simulated using block.
            if (typeof(IDisposable).IsAssignableFrom(controllerType))
            {
                // Pseudocode:
                //     try
                //     {
                //         body();
                //     }
                //     finally
                //     {
                //         (controller as IDisposable).Dispose();
                //     }
                workWithController = Expression.TryFinally(
                    workWithController, 
                    Expression.Call(Expression.TypeAs(controller, typeof(IDisposable)), DisposeMethod));
            }

            bodyContents.Add(workWithController);

            // At the end of the lambda body is the target of return statements.
            bodyContents.Add(Expression.Label(returnTarget, Expression.Constant(Task.FromResult(false))));

            // Build and compile the lambda.
            return Expression.Lambda<RouteHandler<IHttpContext>>(
                Expression.Block(locals, bodyContents),
                contextInLambda,
                routeInLambda,
                cancellationTokenInLambda)
                .Compile();
        }

        private static T AwaitResult<T>(Task<T> task) => task.ConfigureAwait(false).GetAwaiter().GetResult();

        private static T AwaitAndCastResult<T>(string parameterName, Task<object> task)
        {
            var result = task.ConfigureAwait(false).GetAwaiter().GetResult();
            switch (result)
            {
                case null when typeof(T).IsValueType && Nullable.GetUnderlyingType(typeof(T)) == null:
                    throw new InvalidCastException($"Cannot cast null to {typeof(T).FullName} for parameter \"{parameterName}\".");
                case null:
                    return default;
                case T castResult:
                    return castResult;
                default:
                    throw new InvalidCastException($"Cannot cast {result.GetType().FullName} to {typeof(T).FullName} for parameter \"{parameterName}\".");
            }
        }

        private static async Task<bool> TaskToBoolTask(Task result)
        {
            await result.ConfigureAwait(false);
            return true;
        }

        private async Task<bool> SerializeAsyncControllerResultAsync(
            IHttpContext context,
            Task<object> result,
            CancellationToken cancellationToken)
        {
            await Serializer(
                context,
                await result.ConfigureAwait(false),
                cancellationToken).ConfigureAwait(false);

            return true;
        }

        private async Task<bool> SerializeNonAsyncControllerResultAsync(
            IHttpContext context,
            object result,
            CancellationToken cancellationToken)
        {
            await Serializer(context, result, cancellationToken).ConfigureAwait(false);

            return true;
        }

        private Type ValidateControllerType(string argumentName, Type value, bool canBeAbstract)
        {
            value = Validate.NotNull(argumentName, value);
            if (canBeAbstract)
            {
                if (value.IsGenericTypeDefinition
                 || !value.IsSubclassOf(typeof(WebApiController)))
                    throw new ArgumentException($"Controller type must be a subclass of {nameof(WebApiController)}.", argumentName);
            }
            else
            {
                if (value.IsAbstract
                 || value.IsGenericTypeDefinition
                 || !value.IsSubclassOf(typeof(WebApiController)))
                    throw new ArgumentException($"Controller type must be a non-abstract subclass of {nameof(WebApiController)}.", argumentName);
            }

            if (_controllerTypes.Contains(value))
                throw new ArgumentException("Controller type is already registered in this module.", argumentName);

            return value;
        }

        private void RegisterControllerTypeCore(Type controllerType, Expression factoryExpression)
        {
            bool IsValidReturnType(Type type)
                => type == typeof(bool)
                || type == typeof(Task<bool>)
                || type == typeof(object)
                || type == typeof(Task<object>)
                || type == typeof(void)
                || type == typeof(Task);

            var methods = controllerType.GetMethods(BindingFlags.Instance | BindingFlags.Public)
                .Where(m => !m.ContainsGenericParameters && IsValidReturnType(m.ReturnType));

            foreach (var method in methods)
            {
                var attributes = method.GetCustomAttributes(typeof(RouteAttribute))
                    .OfType<RouteAttribute>()
                    .ToArray();
                if (attributes.Length < 1)
                    continue;

                foreach (var attribute in attributes)
                {
                    AddHandler(attribute.Verb, attribute.Route, CompileHandler(factoryExpression, method, attribute.Route));
                }
            }

            _controllerTypes.Add(controllerType);
        }
    }
}