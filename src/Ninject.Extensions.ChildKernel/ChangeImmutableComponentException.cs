namespace Ninject.Extensions.ChildKernel
{
    using System;

    public class ChangeImmutableComponentException : Exception
    {
        public ChangeImmutableComponentException(Type component)
            :base($"Component {nameof(component)} is immutable in childKernel.")
        {  
        }
    }
}
