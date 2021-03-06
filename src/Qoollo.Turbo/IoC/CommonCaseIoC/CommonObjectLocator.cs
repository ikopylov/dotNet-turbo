﻿using System;
using System.Collections.Generic;
using System.Diagnostics.Contracts;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Threading.Tasks;
using Qoollo.Turbo.IoC.Associations;
using Qoollo.Turbo.IoC.Injections;
using Qoollo.Turbo.IoC.ServiceStuff;
using System.Diagnostics;

namespace Qoollo.Turbo.IoC
{
    /// <summary>
    /// Common case IoC container and object locator
    /// </summary>
    [Obsolete("This container is obsolete. Please, use TurboContainer instead.")]
    public class CommonObjectLocator: IObjectLocator<Type>, IDisposable
    {
        private bool _disposeInjectionWithBuilder;
        private bool _useAssocAsDISource;

        private readonly IInjectionResolver _resolver;
        private readonly DirectTypeAssociationContainer _association;
        private readonly TypeStrictInjectionContainer _injection;

        [ContractInvariantMethod]
        private void Invariant()
        {
            Contract.Invariant(_resolver != null);
            Contract.Invariant(_association != null);
            Contract.Invariant(_injection != null);
        }

        /// <summary>
        /// Association container
        /// </summary>
        private class AssociationContainer: DirectTypeAssociationContainer
        {
            private readonly IInjectionResolver _resolver;

            public AssociationContainer(IInjectionResolver resolver)
            {
                Contract.Requires(resolver != null);
                _resolver = resolver;
            }

            protected override Lifetime.LifetimeBase ProduceResolveInfo(Type key, Type objType, Lifetime.Factories.LifetimeFactory val)
            {
                return val.Create(objType, _resolver, null);
            }
        }

        /// <summary>
        /// Internal injection resolver. 
        /// First attempts to resolve injection from the InjectionContainer, then - from the AssociationContainer
        /// </summary>
        private class InjectionThenAssociationResolver : IInjectionResolver
        {
            private readonly TypeStrictInjectionContainer _sourceInj;
            private readonly CommonObjectLocator _curLocator;

            [ContractInvariantMethod]
            private void Invariant()
            {
                Contract.Invariant(_sourceInj != null);
                Contract.Invariant(_curLocator != null);
            }

            /// <summary>
            /// InjectionThenAssociationResolver constructor
            /// </summary>
            /// <param name="srcInj">Injection container</param>
            /// <param name="locator">Owner</param>
            public InjectionThenAssociationResolver(TypeStrictInjectionContainer srcInj, CommonObjectLocator locator)
            {
                Contract.Requires(srcInj != null);
                Contract.Requires(locator != null);

                _sourceInj = srcInj;
                _curLocator = locator;
            }

            public object Resolve(Type reqObjectType, string paramName, Type forType, object extData)
            {
                object res = null;            
                if (_sourceInj.TryGetInjection(reqObjectType, out res))
                    return res;

                return _curLocator.Resolve(reqObjectType);
            }

            public T Resolve<T>(Type forType)
            {
                object res = null;
                if (_sourceInj.TryGetInjection(typeof(T), out res))
                    return (T)res;

                return _curLocator.Resolve<T>();
            }
        }


        /// <summary>
        /// CommonObjectLocator constructor
        /// </summary>
        /// <param name="useAssocAsDISource">Allows object injection from the IoC container itself (not only from InjectionContainer)</param>
        /// <param name="disposeInjectionWithBuilder">Indicates whether the all injected objects should be disposed with the container</param>
        public CommonObjectLocator(bool useAssocAsDISource, bool disposeInjectionWithBuilder)
        {
            _disposeInjectionWithBuilder = disposeInjectionWithBuilder;
            _useAssocAsDISource = useAssocAsDISource;

            _injection = new TypeStrictInjectionContainer(_disposeInjectionWithBuilder);

            if (_useAssocAsDISource)
                _resolver = new InjectionThenAssociationResolver(_injection, this);
            else
                _resolver = _injection.GetDirectInjectionResolver();

            _association = new AssociationContainer(_resolver);
        }

        /// <summary>
        /// CommonObjectLocator constructor
        /// </summary>
        public CommonObjectLocator()
            : this(false, true)
        {
        }



        /// <summary>
        /// Resolves object of the specified type from the container
        /// </summary>
        /// <typeparam name="T">The type of the object to be resolved</typeparam>
        /// <returns>Resolved object</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T Resolve<T>()
        {
            var life = _association.GetAssociation(typeof(T));
            Debug.Assert(life != null);
            return (T)life.GetInstance(_resolver);
        }

        /// <summary>
        /// Attempts to resolves object of the specified type from the container
        /// </summary>
        /// <typeparam name="T">The type of the object to be resolved</typeparam>
        /// <param name="val">Resolved object</param>
        /// <returns>True if the resolution is successful (specified type and all required injections registered in the container); overwise false</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryResolve<T>(out T val)
        {
            Lifetime.LifetimeBase life = null;

            if (_association.TryGetAssociation(typeof(T), out life))
            {
                Debug.Assert(life != null);
                object tmp = null;
                if (life.TryGetInstance(_resolver, out tmp))
                {
                    if (tmp is T)
                    {
                        val = (T)tmp;
                        return true;
                    }
                }
            }

            val = default(T);
            return false;
        }

        /// <summary>
        /// Determines whether the object of the specified type can be resolved by the container
        /// </summary>
        /// <typeparam name="T">The type of an object to be checked</typeparam>
        /// <returns>True if the object can be resolved</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanResolve<T>()
        {
            return _association.Contains(typeof(T));
        }


        /// <summary>
        /// Creates an instance of an object of type 'T' using the default constructor.
        /// The type of an object can be not registered in the container.
        /// </summary>
        /// <typeparam name="T">The type of the object to be created</typeparam>
        /// <returns>Created object</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public T CreateObject<T>()
        {
            return InstantiationService.CreateObject<T>(_resolver);
        }


        /// <summary>
        /// Gets the injection container
        /// </summary>
        public TypeStrictInjectionContainer Injection
        {
            get { return _injection; }
        }

        /// <summary>
        /// Gets the association container
        /// </summary>
        public DirectTypeAssociationContainer Association
        {
            get { return _association; }
        }


        /// <summary>
        /// Resolves object of the specified type from the container
        /// </summary>
        /// <param name="key">The type of the object to be resolved</param>
        /// <returns>Resolved object</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public object Resolve(Type key)
        {
            Contract.Requires(key != null);

            var life = _association.GetAssociation(key);
            Debug.Assert(life != null);
            return life.GetInstance(_resolver);
        }

        /// <summary>
        /// Attempts to resolves object of the specified type from the container
        /// </summary>
        /// <param name="key">The type of the object to be resolved</param>
        /// <param name="val">Resolved object</param>
        /// <returns>True if the resolution is successful (specified type and all required injections registered in the container); overwise false</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool TryResolve(Type key, out object val)
        {
            Contract.Requires((object)key != null);

            Lifetime.LifetimeBase life = null;

            if (_association.TryGetAssociation(key, out life))
            {
                Debug.Assert(life != null);
                if (life.TryGetInstance(_resolver, out val))
                    return true;
            }

            val = null;
            return false;
        }

        /// <summary>
        /// Determines whether the object of the specified type can be resolved by the container
        /// </summary>
        /// <param name="key">The type of an object to be checked</param>
        /// <returns>True if the object can be resolved</returns>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public bool CanResolve(Type key)
        {
            Contract.Requires(key != null);

            return Association.Contains(key);
        }

        /// <summary>
        /// Resolves object from the container for the specified key
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>Resolved object</returns>
        object IObjectLocator<Type>.Resolve(Type key)
        {
            return this.Resolve(key);
        }
        /// <summary>
        /// Attempts to resolve object from the container for the specified key
        /// </summary>
        /// <param name="key">Key</param>
        /// <param name="val">Resolved object</param>
        /// <returns>True if the resolution succeeded; overwise false</returns>
        bool IObjectLocator<Type>.TryResolve(Type key, out object val)
        {
            return this.TryResolve(key, out val);
        }
        /// <summary>
        /// Determines whether the object can be resolved by the container for the specified key
        /// </summary>
        /// <param name="key">Key</param>
        /// <returns>True if the object can be resolved</returns>
        bool IObjectLocator<Type>.CanResolve(Type key)
        {
            return this.CanResolve(key);
        }


        /// <summary>
        /// Cleans-up all resources
        /// </summary>
        /// <param name="isUserCall">True if called by user; false - from finalizer</param>
        protected virtual void Dispose(bool isUserCall)
        {
            if (_association != null && _association is IDisposable)
                (_association as IDisposable).Dispose();

            if (_injection != null && _injection is IDisposable)
                (_injection as IDisposable).Dispose();
        }

        /// <summary>
        /// Cleans-up all resources
        /// </summary>
        public void Dispose()
        {
            this.Dispose(true);
            GC.SuppressFinalize(this);
        }
    }
}
