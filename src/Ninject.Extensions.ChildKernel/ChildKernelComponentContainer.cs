namespace Ninject.Extensions.ChildKernel
{
    using System;
    using System.Collections;
    using System.Collections.Generic;
    using System.Linq;
    using System.Reflection;
    using System.Runtime.ExceptionServices;
    using Ninject.Activation.Caching;
    using Ninject.Components;
    using Ninject.Infrastructure;
    using Ninject.Infrastructure.Disposal;
    using Ninject.Infrastructure.Introspection;
    using Ninject.Selection;
    using Ninject.Selection.Heuristics;

    public class ChildKernelComponentContainer : DisposableObject, IComponentContainer
    {
        private readonly Multimap<Type, Type> _mappings;
        private readonly Dictionary<Type, INinjectComponent> _instances = new Dictionary<Type, INinjectComponent>();
        private readonly HashSet<KeyValuePair<Type, Type>> _transients = new HashSet<KeyValuePair<Type, Type>>();
        private readonly Lazy<MethodInfo> _cast;
        private readonly IComponentContainer _parentComponentContainer;

        private readonly Type[] _immutableComponents;

        /// <summary>
        /// Initializes a new instance of the <see cref="ChildKernelComponentContainer"/> class.
        /// </summary>
        /// <param name="parentComponentContainer">The parent component container.</param>
        public ChildKernelComponentContainer(IComponentContainer parentComponentContainer)
        {
            _parentComponentContainer = parentComponentContainer ?? new ComponentContainer();
            _cast = new Lazy<MethodInfo>(() => typeof(Enumerable).GetMethod("Cast"));

            _mappings = new Multimap<Type, Type>
            {
                {typeof(IActivationCache), typeof(ChildActivationCache)},
                {typeof(IConstructorScorer), typeof(ChildKernelConstructorScorer)},
                {typeof(ISelector), typeof(Selector)}
            };

            _immutableComponents = _mappings.Keys.ToArray();
        }

        /// <summary>
        /// Releases resources held by the object.
        /// </summary>
        /// <param name="disposing"><c>True</c> if called manually, otherwise by GC.</param>
        public override void Dispose(bool disposing)
        {
            if (disposing && !this.IsDisposed)
            {
                foreach (INinjectComponent instance in this._instances.Values)
                {
                    instance.Dispose();
                }

                this._mappings.Clear();
                this._instances.Clear();
            }

            base.Dispose(disposing);
        }

        /// <summary>
        /// Registers a component in the container.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <typeparam name="TImplementation">The component's implementation type.</typeparam>
        public void Add<TComponent, TImplementation>()
            where TComponent : INinjectComponent
            where TImplementation : TComponent, INinjectComponent
        {
            var typeofComponent = typeof(TComponent);
            CheckImmutableComponent(typeofComponent);
            this._mappings.Add(typeofComponent, typeof(TImplementation));
        }

        /// <summary>
        /// Registers a transient component in the container.
        /// </summary>
        /// <typeparam name="TComponent">The component type.</typeparam>
        /// <typeparam name="TImplementation">The component's implementation type.</typeparam>
        public void AddTransient<TComponent, TImplementation>()
            where TComponent : INinjectComponent
            where TImplementation : TComponent, INinjectComponent
        {
            this.Add<TComponent, TImplementation>();
            this._transients.Add(new KeyValuePair<Type, Type>(typeof(TComponent), typeof(TImplementation)));
        }

        /// <summary>
        /// Removes all registrations for the specified component.
        /// </summary>
        /// <typeparam name="T">The component type.</typeparam>
        public void RemoveAll<T>()
            where T : INinjectComponent
        {
            this.RemoveAll(typeof(T));
        }

        /// <summary>
        /// Removes the specified registration.
        /// </summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <typeparam name="TImplementation">The implementation type.</typeparam>
        public void Remove<T, TImplementation>()
            where T : INinjectComponent
            where TImplementation : T
        {
            var typeofT = typeof(T);
            CheckImmutableComponent(typeofT);

            var implementation = typeof(TImplementation);
            if (this._instances.ContainsKey(implementation))
            {
                this._instances[implementation].Dispose();
            }

            this._instances.Remove(implementation);

            this._mappings.Remove(typeofT, typeof(TImplementation));
        }

        /// <summary>
        /// Removes all registrations for the specified component.
        /// </summary>
        /// <param name="component">The component type.</param>
        public void RemoveAll(Type component)
        {
            CheckImmutableComponent(component);

            foreach (Type implementation in this._mappings[component])
            {
                if (this._instances.ContainsKey(implementation))
                {
                    this._instances[implementation].Dispose();
                }

                this._instances.Remove(implementation);
            }

            this._mappings.RemoveAll(component);
        }

        /// <summary>
        /// Gets one instance of the specified component.
        /// </summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>The instance of the component.</returns>
        public T Get<T>()
            where T : INinjectComponent
        {
            return (T)this.Get(typeof(T));
        }

        /// <summary>
        /// Gets all available instances of the specified component.
        /// </summary>
        /// <typeparam name="T">The component type.</typeparam>
        /// <returns>A series of instances of the specified component.</returns>
        public IEnumerable<T> GetAll<T>()
            where T : INinjectComponent
        {
            return this.GetAll(typeof(T)).Cast<T>();
        }

        /// <summary>
        /// Gets one instance of the specified component.
        /// </summary>
        /// <param name="component">The component type.</param>
        /// <returns>The instance of the component.</returns>
        public object Get(Type component)
        {
            if (component == typeof(IKernel))
            {
                return this.Kernel;
            }

            if (component.GetTypeInfo().IsGenericType)
            {
                var gtd = component.GetGenericTypeDefinition();
                var argument = component.GetTypeInfo().GenericTypeArguments[0];

                var info = gtd.GetTypeInfo();
                if (info.IsInterface && typeof(IEnumerable<>).GetTypeInfo().IsAssignableFrom(info))
                {
                    var method = _cast.Value.MakeGenericMethod(argument);
                    return method.Invoke(null, new object[] { this.GetAll(argument) }) as IEnumerable;
                }
            }

            var implementation = this._mappings[component].FirstOrDefault();

            return implementation == null ? _parentComponentContainer.Get(component) : this.ResolveInstance(component, implementation);
        }

        /// <summary>
        /// Gets all available instances of the specified component.
        /// </summary>
        /// <param name="component">The component type.</param>
        /// <returns>A series of instances of the specified component.</returns>
        public IEnumerable<object> GetAll(Type component)
        {
            return this._mappings[component]
                .Select(implementation => this.ResolveInstance(component, implementation))
                .Concat(_parentComponentContainer.GetAll(component));
        }

        /// <summary>
        /// The kernel
        /// </summary>
        public IKernel Kernel { get; set; }

        private static ConstructorInfo SelectConstructor(Type component, Type implementation)
        {
            var constructor =
                implementation.GetTypeInfo().DeclaredConstructors.Where(c => c.IsPublic && !c.IsStatic).OrderByDescending(c => c.GetParameters().Length).
                    FirstOrDefault();

            if (constructor == null)
            {
                throw new InvalidOperationException(ExceptionFormatter.NoConstructorsAvailableForComponent(component, implementation));
            }

            return constructor;
        }

        private object ResolveInstance(Type component, Type implementation)
        {
            lock (this._instances)
            {
                return this._instances.ContainsKey(implementation) ? this._instances[implementation] : this.CreateNewInstance(component, implementation);
            }
        }

        private object CreateNewInstance(Type component, Type implementation)
        {
            var constructor = SelectConstructor(component, implementation);
            var arguments = constructor.GetParameters().Select(parameter => this.Get(parameter.ParameterType)).ToArray();

            try
            {
                var instance = constructor.Invoke(arguments) as INinjectComponent;

                // Todo: Clone Settings during kernel build (is this still important? Can clone settings now)
                instance.Settings = this.Kernel.Settings;

                if (!this._transients.Contains(new KeyValuePair<Type, Type>(component, implementation)))
                {
                    this._instances.Add(implementation, instance);
                }

                return instance;
            }
            catch (TargetInvocationException ex)
            {
                var innerException = ex.InnerException;
                ExceptionDispatchInfo.Capture(innerException).Throw();

                return null;
            }
        }

        private void CheckImmutableComponent(Type component)
        {
            if (_immutableComponents.Any(t => t == component))
            {
                throw new ChangeImmutableComponentException(component);
            }
        }
    }
}
