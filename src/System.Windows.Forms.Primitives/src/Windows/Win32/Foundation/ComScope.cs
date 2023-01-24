﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Runtime.CompilerServices;
using Windows.Win32.System.Com;

namespace Windows.Win32.Foundation
{
    /// <summary>
    ///  Lifetime management struct for a native COM pointer. Meant to be utilized in a <see langword="using"/> statement
    ///  to ensure <see cref="IUnknown.Release"/> is called when going out of scope with the using.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   This struct has implicit conversions to T** and void** so it can be passed directly to out methods.
    ///   For example:
    ///  </para>
    ///  <code>
    ///   using ComScope&lt;IUnknown&gt; unknown = new(null);
    ///   comObject-&gt;QueryInterface(&amp;iid, unknown);
    ///  </code>
    ///  <para>
    ///   Take care to NOT make copies of the struct to avoid accidental over-release.
    ///  </para>
    /// </remarks>
    /// <typeparam name="T">
    ///  This should be one of the struct COM definitions as generated by CsWin32. Ideally we'd constrain to
    ///  <see cref="IUnknown.Interface"/> or some other interface tag to enforce that this is being used around
    ///  a struct that is actually a COM wrapper.
    /// </typeparam>
    internal readonly unsafe ref struct ComScope<T> where T : unmanaged, IComIID
    {
        // Keeping internal as nint allows us to use Unsafe methods to get significantly better generated code.
        private readonly nint _value;
        public T* Value => (T*)_value;
        public IUnknown* AsUnknown => (IUnknown*)_value;

        public ComScope(T* value) => _value = (nint)value;

        // Can't add an operator for IUnknown* as we have ComScope<IUnknown>.

        public static implicit operator T*(in ComScope<T> scope) => (T*)scope._value;

        public static implicit operator void*(in ComScope<T> scope) => (void*)scope._value;

        public static implicit operator nint(in ComScope<T> scope) => scope._value;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator T**(in ComScope<T> scope) => (T**)Unsafe.AsPointer(ref Unsafe.AsRef(scope._value));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static implicit operator void**(in ComScope<T> scope) => (void**)Unsafe.AsPointer(ref Unsafe.AsRef(scope._value));

        public bool IsNull => _value == 0;

        public ComScope<TTo> TryQuery<TTo>(out HRESULT hr) where TTo : unmanaged, IComIID
        {
            ComScope<TTo> scope = new(null);
            hr = ((IUnknown*)Value)->QueryInterface(IID.Get<TTo>(), scope);
            return scope;
        }

        /// <summary>
        ///  Attempt to create a <see cref="ComScope{T}"/> from the given COM interface.
        /// </summary>
        public static ComScope<T> TryQueryFrom<TFrom>(TFrom* from, out HRESULT hr) where TFrom : unmanaged, IComIID
        {
            ComScope<T> scope = new(null);
            hr = from is null ? HRESULT.E_POINTER : ((IUnknown*)from)->QueryInterface(IID.Get<T>(), scope);
            return scope;
        }

        /// <summary>
        ///  Create a <see cref="ComScope{T}"/> from the given COM interface. Throws on failure.
        /// </summary>
        public static ComScope<T> QueryFrom<TFrom>(TFrom* from) where TFrom : unmanaged, IComIID
        {
            if (from is null)
            {
                HRESULT.E_POINTER.ThrowOnFailure();
            }

            ComScope<T> scope = new(null);
            ((IUnknown*)from)->QueryInterface(IID.Get<T>(), scope).ThrowOnFailure();
            return scope;
        }

        public void Dispose()
        {
            IUnknown* unknown = (IUnknown*)_value;

            // Really want this to be null after disposal to avoid double releases, but we also want
            // to maintain the readonly state of the struct to allow passing as `in` without creating implicit
            // copies (which would break the T** and void** operators).
            *(void**)this = null;
            if (unknown is not null)
            {
                unknown->Release();
            }
        }
    }
}
