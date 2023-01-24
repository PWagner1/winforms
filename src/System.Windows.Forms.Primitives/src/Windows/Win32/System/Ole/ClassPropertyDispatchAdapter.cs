﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.
// See the LICENSE file in the project root for more information.

using System.Diagnostics;
using System.Diagnostics.CodeAnalysis;
using System.Reflection;
using System.Runtime.InteropServices;
using Windows.Win32.System.Com;
using InteropMarshal = System.Runtime.InteropServices.Marshal;

namespace Windows.Win32.System.Ole;

/// <summary>
///  Provides an <see cref="IDispatchEx"/> friendly view of a given class' public properties.
/// </summary>
internal unsafe class ClassPropertyDispatchAdapter
{
    private const int StartingDispId = 0x00010000;
    private int _nextDispId = StartingDispId;

    private readonly WeakReference<object> _instance;

    [DynamicallyAccessedMembers(DynamicallyAccessedMemberTypes.PublicProperties)]
    private readonly Type _type;

    private readonly Dictionary<int, DispatchEntry> _members = new();
    private readonly Dictionary<string, int> _reverseLookup = new(StringComparer.OrdinalIgnoreCase);

    private readonly ClassPropertyDispatchAdapter? _priorAdapter;

    /// <param name="priorAdapter">
    ///  A prior adapter for chaining. This adapter will be consulted first for all results.
    /// </param>
    public ClassPropertyDispatchAdapter(object instance, ClassPropertyDispatchAdapter? priorAdapter = null)
    {
        ArgumentNullException.ThrowIfNull(instance);
        _instance = new(instance);
        _type = instance.GetType();
        _priorAdapter = priorAdapter;

        var properties = _type.GetProperties(BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static);
        foreach (var property in properties)
        {
            var (name, dispId, flags) = GetPropertyInfo(property);
            dispId = GetUnusedDispId(dispId);

            if (NameInUse(name))
            {
                Debug.WriteLine($"Already found name {name}");
                continue;
            }

            _members.Add(
                dispId,
                new()
                {
                    DispId = dispId,
                    Flags = flags,
                    Name = name
                });

            _reverseLookup.Add(name, dispId);
        }
    }

    private bool NameInUse(string name)
        => _reverseLookup.ContainsKey(name) || (_priorAdapter is { } prior && prior.NameInUse(name));

    private bool IdInUse(int id)
        => _members.ContainsKey(id) || (_priorAdapter is { } prior && prior.IdInUse(id));

    private int GetUnusedDispId(int desiredId)
    {
        if (desiredId != PInvoke.DISPID_UNKNOWN && !_members.ContainsKey(desiredId))
        {
            return desiredId;
        }

        do
        {
            desiredId = _nextDispId;
            _nextDispId++;
        }
        while (IdInUse(desiredId));

        return desiredId;
    }

    /// <summary>
    ///  Try to find the DISPID for a given <paramref name="name"/>. Searches are case insensitive.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matches up to <see cref="IDispatchEx.GetDispID(BSTR, uint, int*)"/>
    ///  </para>
    /// </remarks>
    /// <param name="dispId">The DISPID, if found.</param>
    /// <returns><see langword="true"/> if the given <paramref name="name"/> is found.</returns>
    public bool TryGetDispID(string name, out int dispId)
        => (_priorAdapter is { } prior && prior.TryGetDispID(name, out dispId))
            || _reverseLookup.TryGetValue(name, out dispId);

    /// <summary>
    ///  Try to find the member name for a given <paramref name="dispId"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matches up to <see cref="IDispatchEx.GetMemberName(IDispatchEx*, int, BSTR*)"/>
    ///  </para>
    /// </remarks>
    /// <param name="name">The name, if found.</param>
    /// <returns><see langword="true"/> if the given <paramref name="dispId"/> is found.</returns>
    public bool TryGetMemberName(int dispId, [NotNullWhen(true)] out string? name)
    {
        if (_priorAdapter is { } prior && prior.TryGetMemberName(dispId, out name))
        {
            return true;
        }

        if (_members.TryGetValue(dispId, out DispatchEntry value))
        {
            name = value.Name;
            return true;
        }

        name = null;
        return false;
    }

    /// <summary>
    ///  Attempts to invoke the given <paramref name="dispId"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matches up to <see cref="IDispatchEx.InvokeEx(IDispatchEx*, int, uint, ushort, DISPPARAMS*, VARIANT*, EXCEPINFO*, Com.IServiceProvider*)"/>
    ///  </para>
    /// </remarks>
    [UnconditionalSuppressMessage(
        "AssemblyLoadTrimming",
        "IL2080:RequiresUnreferencedCode",
        Justification = "_type is correctly attributed as only using public properties")]
    public HRESULT Invoke(
        int dispId,
        uint lcid,
        DISPATCH_FLAGS flags,
        DISPPARAMS* parameters,
        VARIANT* result)
    {
        if (_priorAdapter is { } prior)
        {
            HRESULT hr = prior.Invoke(dispId, lcid, flags, parameters, result);
            if (hr.Succeeded)
            {
                return hr;
            }
        }

        BindingFlags bindingFlags = BindingFlags.Public | BindingFlags.Instance | BindingFlags.Static;

        if (flags.HasFlag(DISPATCH_FLAGS.DISPATCH_PROPERTYPUT | DISPATCH_FLAGS.DISPATCH_PROPERTYPUTREF))
        {
            bindingFlags |= BindingFlags.SetProperty;
        }
        else if (flags.HasFlag(DISPATCH_FLAGS.DISPATCH_PROPERTYPUT))
        {
            bindingFlags |= BindingFlags.PutDispProperty;
        }
        else if (flags.HasFlag(DISPATCH_FLAGS.DISPATCH_PROPERTYPUTREF))
        {
            bindingFlags |= BindingFlags.PutRefDispProperty;
        }
        else
        {
            bindingFlags |= BindingFlags.GetProperty;
        }

        Debug.Assert(!bindingFlags.HasFlag(BindingFlags.NonPublic));

        if (!_members.TryGetValue(dispId, out var entry))
        {
            return HRESULT.DISP_E_MEMBERNOTFOUND;
        }

        if (!_instance.TryGetTarget(out object? target))
        {
            return HRESULT.COR_E_OBJECTDISPOSED;
        }

        object? resultObject = null;

        if (bindingFlags.HasFlag(BindingFlags.PutDispProperty))
        {
            if (parameters->cArgs != 1)
            {
                return HRESULT.DISP_E_BADPARAMCOUNT;
            }

            try
            {
                object? value = InteropMarshal.GetObjectForNativeVariant((nint)parameters->rgvarg);
                resultObject = _type.InvokeMember(
                    entry.Name,
                    bindingFlags,
                    binder: null,
                    target,
                    new object?[] { value });
            }
            catch (Exception ex)
            {
                return (HRESULT)ex.HResult;
            }
        }
        else
        {
            if (result is null)
            {
                return HRESULT.E_POINTER;
            }

            try
            {
                resultObject = _type.InvokeMember(
                    entry.Name,
                    bindingFlags,
                    binder: null,
                    target,
                    args: null);

                InteropMarshal.GetNativeVariantForObject(resultObject, (nint)result);
            }
            catch (Exception ex)
            {
                return (HRESULT)ex.HResult;
            }
        }

        return HRESULT.S_OK;
    }

    /// <summary>
    ///  Try to find the next logical DISPID after the given <paramref name="dispId"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matches up to <see cref="IDispatchEx.GetNextDispID(IDispatchEx*, uint, int, int*)"/>
    ///  </para>
    /// </remarks>
    /// <param name="nextDispId">The DISPID, if found.</param>
    /// <returns><see langword="true"/> if the next DISPID after <paramref name="dispId"/> is found.</returns>
    public bool TryGetNextDispId(int dispId, out int nextDispId)
    {
        if (_priorAdapter is { } prior && prior.TryGetNextDispId(dispId, out nextDispId))
        {
            return true;
        }

        bool foundLast = dispId == PInvoke.DISPID_STARTENUM;

        foreach (int currentId in _members.Keys)
        {
            if (foundLast)
            {
                nextDispId = currentId;
                return true;
            }

            foundLast = dispId == currentId;
        }

        nextDispId = PInvoke.DISPID_UNKNOWN;
        return false;
    }

    /// <summary>
    ///  Try to get the member properties for the given <paramref name="dispId"/>.
    /// </summary>
    /// <remarks>
    ///  <para>
    ///   Matches up to <see cref="IDispatchEx.GetMemberProperties(int, uint, FDEX_PROP_FLAGS*)"/>
    ///  </para>
    /// </remarks>
    /// <returns><see langword="true"/> if the <paramref name="dispId"/> is found.</returns>
    public bool TryGetMemberProperties(int dispId, out FDEX_PROP_FLAGS flags)
    {
        if (_priorAdapter is { } prior && prior.TryGetMemberProperties(dispId, out flags))
        {
            return true;
        }

        if (_members.TryGetValue(dispId, out DispatchEntry value))
        {
            flags = value.Flags;
            return true;
        }

        flags = default;
        return false;
    }

    // Somewhat surprisingly, IReflect doesn't map property names back to the original type, so Invokes back through
    // IDispatch/Ex would come in with a Get/SetProperty flags instead of Get/SetField. There is no way to specify
    // a field via IDispatch/Ex, so IReflect implementers would have to track this case.

    // private static (string Name, int Dispid, FDEX_PROP_FLAGS Flags) GetFieldInfo(FieldInfo info)
    // {
    //     int dispid = info.GetCustomAttribute<DispIdAttribute>()?.Value ?? Interop.DISPID_UNKNOWN;
    //     string name = info.Name;
    //     FDEX_PROP_FLAGS flags =
    //         FDEX_PROP_FLAGS.fdexPropCanGet
    //         | FDEX_PROP_FLAGS.fdexPropCanPut
    //         | FDEX_PROP_FLAGS.fdexPropCannotPutRef
    //         | FDEX_PROP_FLAGS.fdexPropCannotCall
    //         | FDEX_PROP_FLAGS.fdexPropCannotConstruct
    //         | FDEX_PROP_FLAGS.fdexPropCannotSourceEvents;
    //
    //     return (name, dispid, flags);
    // }

    private static (string Name, int DispId, FDEX_PROP_FLAGS Flags) GetPropertyInfo(PropertyInfo info)
    {
        int dispid = info.GetCustomAttribute<DispIdAttribute>()?.Value ?? PInvoke.DISPID_UNKNOWN;
        string name = info.Name;
        FDEX_PROP_FLAGS flags =
            (info.CanRead ? FDEX_PROP_FLAGS.fdexPropCanGet : FDEX_PROP_FLAGS.fdexPropCannotGet)
            | (info.CanWrite ? FDEX_PROP_FLAGS.fdexPropCanPut : FDEX_PROP_FLAGS.fdexPropCannotPut)
            | FDEX_PROP_FLAGS.fdexPropCannotPutRef
            | FDEX_PROP_FLAGS.fdexPropCannotCall
            | FDEX_PROP_FLAGS.fdexPropCannotConstruct
            | FDEX_PROP_FLAGS.fdexPropCannotSourceEvents;

        return (name, dispid, flags);
    }

    private struct DispatchEntry
    {
        public string Name;
        public FDEX_PROP_FLAGS Flags;
        public int DispId;
    }
}
