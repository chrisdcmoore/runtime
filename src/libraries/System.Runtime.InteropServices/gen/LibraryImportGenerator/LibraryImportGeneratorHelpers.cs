﻿// Licensed to the .NET Foundation under one or more agreements.
// The .NET Foundation licenses this file to you under the MIT license.

using System;
using System.Collections.Generic;
using System.Collections.Immutable;
using System.Linq;
using System.Text;
using Microsoft.CodeAnalysis;

namespace Microsoft.Interop
{
    internal static class LibraryImportGeneratorHelpers
    {
        public static MarshallingInfoParser CreateMarshallingInfoParser(StubEnvironment env, TargetFrameworkSettings tf, GeneratorDiagnosticsBag diagnostics, IMethodSymbol method, InteropAttributeCompilationData interopAttributeData, AttributeData unparsedAttributeData)
        {
            // Compute the current default string encoding value.
            CharEncoding defaultEncoding = CharEncoding.Undefined;
            if (interopAttributeData.IsUserDefined.HasFlag(InteropAttributeMember.StringMarshalling))
            {
                defaultEncoding = interopAttributeData.StringMarshalling switch
                {
                    StringMarshalling.Utf16 => CharEncoding.Utf16,
                    StringMarshalling.Utf8 => CharEncoding.Utf8,
                    StringMarshalling.Custom => CharEncoding.Custom,
                    _ => CharEncoding.Undefined, // [Compat] Do not assume a specific value
                };
            }
            else if (interopAttributeData.IsUserDefined.HasFlag(InteropAttributeMember.StringMarshallingCustomType))
            {
                defaultEncoding = CharEncoding.Custom;
            }

            var defaultInfo = new DefaultMarshallingInfo(defaultEncoding, interopAttributeData.StringMarshallingCustomType);

            var useSiteAttributeParsers = ImmutableArray.Create<IUseSiteAttributeParser>(
                    new MarshalAsAttributeParser(diagnostics, defaultInfo),
                    new MarshalUsingAttributeParser(env.Compilation, diagnostics));

            IMarshallingInfoAttributeParser marshalAsAttributeParser = new MarshalAsAttributeParser(diagnostics, defaultInfo);

            if (tf.TargetFramework == TargetFramework.Net && tf.Version.Major >= 7)
            {
                // If we have support for the attributed marshalling model, then we want to use that to provide the marshalling logic
                // when possible. On other target frameworks, we'll fall back to using the Forwarder logic and re-emitting the MarshalAs attribute.
                marshalAsAttributeParser = new MarshalAsWithCustomMarshallersParser(env.Compilation, diagnostics, marshalAsAttributeParser);
            }

            return new MarshallingInfoParser(
                diagnostics,
                new MethodSignatureElementInfoProvider(env.Compilation, diagnostics, method, useSiteAttributeParsers),
                useSiteAttributeParsers,
                ImmutableArray.Create(
                    marshalAsAttributeParser,
                    new MarshalUsingAttributeParser(env.Compilation, diagnostics),
                    new NativeMarshallingAttributeParser(env.Compilation, diagnostics),
                    new ComInterfaceMarshallingInfoProvider(env.Compilation)),
                ImmutableArray.Create<ITypeBasedMarshallingInfoProvider>(
                    new SafeHandleMarshallingInfoProvider(env.Compilation, method.ContainingType),
                    new ArrayMarshallingInfoProvider(env.Compilation),
                    new CharMarshallingInfoProvider(defaultInfo),
                    new StringMarshallingInfoProvider(env.Compilation, diagnostics, unparsedAttributeData, defaultInfo),
                    new BooleanMarshallingInfoProvider(),
                    new BlittableTypeMarshallingInfoProvider(env.Compilation)));
        }

        public static IMarshallingGeneratorFactory CreateGeneratorFactory(TargetFrameworkSettings tf, LibraryImportGeneratorOptions options, EnvironmentFlags env)
        {
            IMarshallingGeneratorFactory generatorFactory;

            if (options.GenerateForwarders)
            {
                generatorFactory = new ForwarderMarshallingGeneratorFactory();
            }
            else
            {
                if (tf.TargetFramework != TargetFramework.Net || tf.Version.Major < 7)
                {
                    // If we're using our downstream support, fall back to the Forwarder marshaller when the TypePositionInfo is unhandled.
                    generatorFactory = new ForwarderMarshallingGeneratorFactory();
                }
                else
                {
                    // If we're in a "supported" scenario, then emit a diagnostic as our final fallback.
                    generatorFactory = new UnsupportedMarshallingFactory();
                }

                generatorFactory = new NoMarshallingInfoErrorMarshallingFactory(generatorFactory, TypeNames.LibraryImportAttribute_ShortName);

                // Since the char type can go into the P/Invoke signature here, we can only use it when
                // runtime marshalling is disabled.
                generatorFactory = new CharMarshallingGeneratorFactory(generatorFactory, useBlittableMarshallerForUtf16: env.HasFlag(EnvironmentFlags.DisableRuntimeMarshalling), TypeNames.LibraryImportAttribute_ShortName);

                InteropGenerationOptions interopGenerationOptions = new(options.UseMarshalType);
                generatorFactory = new MarshalAsMarshallingGeneratorFactory(interopGenerationOptions, generatorFactory);

                if (tf.TargetFramework == TargetFramework.Net || tf.Version.Major >= 7)
                {
                    IMarshallingGeneratorFactory elementFactory = new AttributedMarshallingModelGeneratorFactory(
                        // Since the char type in an array will not be part of the P/Invoke signature, we can
                        // use the regular blittable marshaller in all cases.
                        new CharMarshallingGeneratorFactory(generatorFactory, useBlittableMarshallerForUtf16: true, TypeNames.LibraryImportAttribute_ShortName),
                        new AttributedMarshallingModelOptions(env.HasFlag(EnvironmentFlags.DisableRuntimeMarshalling), MarshalMode.ElementIn, MarshalMode.ElementRef, MarshalMode.ElementOut));
                    // We don't need to include the later generator factories for collection elements
                    // as the later generator factories only apply to parameters.
                    generatorFactory = new AttributedMarshallingModelGeneratorFactory(
                        generatorFactory,
                        elementFactory,
                        new AttributedMarshallingModelOptions(env.HasFlag(EnvironmentFlags.DisableRuntimeMarshalling), MarshalMode.ManagedToUnmanagedIn, MarshalMode.ManagedToUnmanagedRef, MarshalMode.ManagedToUnmanagedOut));
                }

                generatorFactory = new ByValueContentsMarshalKindValidator(generatorFactory);
            }

            return generatorFactory;
        }
    }
}
