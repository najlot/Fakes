using System.Collections.Immutable;
using System.CodeDom.Compiler;
using System.Text;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using Microsoft.CodeAnalysis.CSharp.Syntax;

namespace Najlot.Fakes.Generators;

[Generator]
public sealed class FakeGenerator : IIncrementalGenerator
{
	private const string FakeAttributeMetadataName = "Najlot.Fakes.Attributes.FakeAttribute";
	private const string AssignedValueParameterName = "assignedValue";

	private static readonly SymbolDisplayFormat TypeNameFormat = new(
		globalNamespaceStyle: SymbolDisplayGlobalNamespaceStyle.Included,
		typeQualificationStyle: SymbolDisplayTypeQualificationStyle.NameAndContainingTypesAndNamespaces,
		genericsOptions: SymbolDisplayGenericsOptions.IncludeTypeParameters,
		miscellaneousOptions: SymbolDisplayMiscellaneousOptions.EscapeKeywordIdentifiers |
							  SymbolDisplayMiscellaneousOptions.IncludeNullableReferenceTypeModifier);

	private static readonly DiagnosticDescriptor TypeMustBePartialDescriptor = new(
		id: "FAKE001",
		title: "Fake type must be partial",
		messageFormat: "Type '{0}' must be partial to use [Fake]",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor UnsupportedTargetTypeDescriptor = new(
		id: "FAKE002",
		title: "Fake target type is not supported",
		messageFormat: "Type '{0}' is not supported by [Fake]: {1}",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor ContainingTypeMustBePartialDescriptor = new(
		id: "FAKE003",
		title: "Containing type must be partial",
		messageFormat: "Containing type '{0}' must be partial when a nested fake type is generated",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor NoMembersToGenerateDescriptor = new(
		id: "FAKE004",
		title: "No fakeable members were found",
		messageFormat: "Type '{0}' does not require any interface or abstract member implementations",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor UnsupportedMemberDescriptor = new(
		id: "FAKE005",
		title: "Inherited member is not supported",
		messageFormat: "Member '{0}' is not supported by [Fake]: {1}",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	private static readonly DiagnosticDescriptor ConflictingMemberDescriptor = new(
		id: "FAKE006",
		title: "Inherited members conflict",
		messageFormat: "Member '{0}' cannot be generated because inherited requirements conflict: {1}",
		category: "Fakes",
		defaultSeverity: DiagnosticSeverity.Error,
		isEnabledByDefault: true);

	public void Initialize(IncrementalGeneratorInitializationContext context)
	{
		var candidateTypes = context.SyntaxProvider.CreateSyntaxProvider(
			static (node, _) => node is TypeDeclarationSyntax { AttributeLists.Count: > 0 },
			static (generatorContext, _) => (TypeDeclarationSyntax)generatorContext.Node);

		var compilationAndCandidates = context.CompilationProvider.Combine(candidateTypes.Collect());

		context.RegisterSourceOutput(compilationAndCandidates, static (productionContext, source) =>
		{
			var compilation = source.Left;
			var candidates = source.Right;

			var fakeAttributeSymbol = compilation.GetTypeByMetadataName(FakeAttributeMetadataName);
			if (fakeAttributeSymbol is null)
			{
				return;
			}

			var processedTypes = new HashSet<INamedTypeSymbol>(SymbolEqualityComparer.Default);

			foreach (var candidate in candidates)
			{
				var semanticModel = compilation.GetSemanticModel(candidate.SyntaxTree);
				if (semanticModel.GetDeclaredSymbol(candidate) is not INamedTypeSymbol typeSymbol)
				{
					continue;
				}

				if (!processedTypes.Add(typeSymbol) || !HasFakeAttribute(typeSymbol, fakeAttributeSymbol))
				{
					continue;
				}

				GenerateType(productionContext, typeSymbol, candidate);
			}
		});
	}

	private static void GenerateType(SourceProductionContext context, INamedTypeSymbol typeSymbol, TypeDeclarationSyntax declarationSyntax)
	{
		if (!HasPartialModifier(declarationSyntax))
		{
			context.ReportDiagnostic(Diagnostic.Create(
				TypeMustBePartialDescriptor,
				declarationSyntax.Identifier.GetLocation(),
				typeSymbol.Name));
			return;
		}

		if (HasFileModifier(declarationSyntax))
		{
			context.ReportDiagnostic(Diagnostic.Create(
				UnsupportedTargetTypeDescriptor,
				declarationSyntax.Identifier.GetLocation(),
				typeSymbol.Name,
				"file-local types cannot be extended from generated source"));
			return;
		}

		if (!IsSupportedTargetType(declarationSyntax, typeSymbol, out var unsupportedReason))
		{
			context.ReportDiagnostic(Diagnostic.Create(
				UnsupportedTargetTypeDescriptor,
				declarationSyntax.Identifier.GetLocation(),
				typeSymbol.Name,
				unsupportedReason));
			return;
		}

		foreach (var containingType in GetContainingTypes(typeSymbol))
		{
			if (TryGetDeclarationSyntax(containingType) is not TypeDeclarationSyntax containingSyntax)
			{
				continue;
			}

			if (!HasPartialModifier(containingSyntax))
			{
				context.ReportDiagnostic(Diagnostic.Create(
					ContainingTypeMustBePartialDescriptor,
					containingSyntax.Identifier.GetLocation(),
					containingType.Name));
				return;
			}

			if (HasFileModifier(containingSyntax))
			{
				context.ReportDiagnostic(Diagnostic.Create(
					UnsupportedTargetTypeDescriptor,
					containingSyntax.Identifier.GetLocation(),
					containingType.Name,
					"file-local containing types cannot host generated nested fake types"));
				return;
			}
		}

		var collection = CollectMembers(typeSymbol);
		foreach (var diagnostic in collection.Diagnostics)
		{
			context.ReportDiagnostic(diagnostic);
		}

		if (collection.Methods.Count == 0 && collection.Properties.Count == 0 && collection.Events.Count == 0)
		{
			context.ReportDiagnostic(Diagnostic.Create(
				NoMembersToGenerateDescriptor,
				declarationSyntax.Identifier.GetLocation(),
				typeSymbol.Name));
			return;
		}

		var nameAllocator = new NameAllocator(typeSymbol.GetMembers().Select(static member => member.Name));

		var methodNameCounts = collection.Methods
			.GroupBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

		var propertyNameCounts = collection.Properties
			.GroupBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal)
			.ToDictionary(static group => group.Key, static group => group.Count(), StringComparer.Ordinal);

		var methodPublicNames = CreateMethodPublicNameMap(collection.Methods, nameAllocator);

		var methodPlans = ApplyMethodEmissionRules(collection.Methods
			.OrderBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal)
			.ThenBy(static requirement => GetMethodIdentityKey(requirement.Symbol), StringComparer.Ordinal)
			.Select(requirement => CreateMethodPlan(requirement, nameAllocator, methodPublicNames[requirement.Symbol.Name], methodNameCounts[requirement.Symbol.Name] > 1))
			.ToArray());

		var propertyPlans = collection.Properties
			.OrderBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal)
			.ThenBy(static requirement => GetPropertyIdentityKey(requirement.Symbol), StringComparer.Ordinal)
			.Select(requirement => CreatePropertyPlan(requirement, nameAllocator, propertyNameCounts[requirement.Symbol.Name] > 1))
			.ToArray();

		var eventPlans = collection.Events
			.OrderBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal)
			.Select(requirement => CreateEventPlan(requirement, nameAllocator))
			.ToArray();

		var typeKeyHelperName = methodPlans.Any(static plan => plan.HandlerMapName is not null)
			? nameAllocator.Reserve("CreateFakeTypeKey")
			: null;

		var source = BuildSource(typeSymbol, declarationSyntax, methodPlans, propertyPlans, eventPlans, typeKeyHelperName);
		var hintName = BuildHintName(typeSymbol);
		context.AddSource(hintName, source);
	}

	private static MemberCollection CollectMembers(INamedTypeSymbol typeSymbol)
	{
		var result = new MemberCollection();
		var methods = new Dictionary<string, MethodRequirement>(StringComparer.Ordinal);
		var properties = new Dictionary<string, PropertyRequirement>(StringComparer.Ordinal);
		var events = new Dictionary<string, EventRequirement>(StringComparer.Ordinal);

		foreach (var interfaceSymbol in typeSymbol.AllInterfaces.OrderBy(static symbol => symbol.ToDisplayString(), StringComparer.Ordinal))
		{
			foreach (var member in interfaceSymbol.GetMembers())
			{
				switch (member)
				{
					case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.Ordinary && methodSymbol.IsAbstract && !methodSymbol.IsStatic:
						if (typeSymbol.FindImplementationForInterfaceMember(methodSymbol) is null)
						{
							AddMethodRequirement(result, methods, methodSymbol, requiresOverride: false, requiresPublic: true);
						}
						break;

					case IPropertySymbol propertySymbol when !propertySymbol.IsStatic:
						if (typeSymbol.FindImplementationForInterfaceMember(propertySymbol) is null)
						{
							var getterRequired = propertySymbol.GetMethod is { IsAbstract: true };
							var setterRequired = propertySymbol.SetMethod is { IsAbstract: true };
							if (getterRequired || setterRequired)
							{
								AddPropertyRequirement(result, properties, propertySymbol, getterRequired, setterRequired, requiresOverride: false, requiresPublic: true);
							}
						}
						break;

					case IEventSymbol eventSymbol when !eventSymbol.IsStatic:
						if (typeSymbol.FindImplementationForInterfaceMember(eventSymbol) is null)
						{
							var addRequired = eventSymbol.AddMethod is { IsAbstract: true };
							var removeRequired = eventSymbol.RemoveMethod is { IsAbstract: true };
							if (addRequired || removeRequired)
							{
								AddEventRequirement(result, events, eventSymbol, requiresOverride: false, requiresPublic: true);
							}
						}
						break;
				}
			}
		}

		for (var baseType = typeSymbol.BaseType; baseType is not null; baseType = baseType.BaseType)
		{
			foreach (var member in baseType.GetMembers())
			{
				switch (member)
				{
					case IMethodSymbol methodSymbol when methodSymbol.MethodKind == MethodKind.Ordinary && methodSymbol.IsAbstract && !methodSymbol.IsStatic:
						if (!IsAbstractMemberImplemented(typeSymbol, methodSymbol))
						{
							AddMethodRequirement(result, methods, methodSymbol, requiresOverride: true, requiresPublic: false);
						}
						break;

					case IPropertySymbol propertySymbol when !propertySymbol.IsStatic:
						var getterRequired = propertySymbol.GetMethod is { IsAbstract: true };
						var setterRequired = propertySymbol.SetMethod is { IsAbstract: true };
						if ((getterRequired || setterRequired) && !IsAbstractMemberImplemented(typeSymbol, propertySymbol))
						{
							AddPropertyRequirement(result, properties, propertySymbol, getterRequired, setterRequired, requiresOverride: true, requiresPublic: false);
						}
						break;

					case IEventSymbol eventSymbol when eventSymbol.IsAbstract && !eventSymbol.IsStatic:
						if (!IsAbstractMemberImplemented(typeSymbol, eventSymbol))
						{
							AddEventRequirement(result, events, eventSymbol, requiresOverride: true, requiresPublic: false);
						}
						break;
				}
			}
		}

		result.Methods.AddRange(methods.Values);
		result.Properties.AddRange(properties.Values);
		result.Events.AddRange(events.Values);
		return result;
	}

	private static void AddMethodRequirement(
		MemberCollection collection,
		Dictionary<string, MethodRequirement> methods,
		IMethodSymbol methodSymbol,
		bool requiresOverride,
		bool requiresPublic)
	{
		if (!IsSupportedMethod(methodSymbol, out var unsupportedReason))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				UnsupportedMemberDescriptor,
				methodSymbol.Locations.FirstOrDefault(),
				methodSymbol.ToDisplayString(),
				unsupportedReason));
			return;
		}

		var key = GetMethodIdentityKey(methodSymbol);
		if (!methods.TryGetValue(key, out var existingRequirement))
		{
			methods.Add(key, new MethodRequirement(methodSymbol, requiresOverride, requiresPublic));
			return;
		}

		if (!AreEquivalentMethodShapes(existingRequirement.Symbol, methodSymbol))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				methodSymbol.Locations.FirstOrDefault(),
				methodSymbol.ToDisplayString(),
				"matching method signatures require different return shapes"));
			return;
		}

		existingRequirement.RequiresPublic |= requiresPublic;
		if (!existingRequirement.RequiresOverride && requiresOverride)
		{
			existingRequirement.Symbol = methodSymbol;
			existingRequirement.RequiresOverride = true;
		}

		if (existingRequirement.RequiresOverride && existingRequirement.RequiresPublic && existingRequirement.Symbol.DeclaredAccessibility != Accessibility.Public)
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				existingRequirement.Symbol.Locations.FirstOrDefault(),
				existingRequirement.Symbol.ToDisplayString(),
				"a non-public abstract member cannot also satisfy a public interface contract implicitly"));
		}
	}

	private static void AddPropertyRequirement(
		MemberCollection collection,
		Dictionary<string, PropertyRequirement> properties,
		IPropertySymbol propertySymbol,
		bool getterRequired,
		bool setterRequired,
		bool requiresOverride,
		bool requiresPublic)
	{
		if (!IsSupportedProperty(propertySymbol, getterRequired, setterRequired, out var unsupportedReason))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				UnsupportedMemberDescriptor,
				propertySymbol.Locations.FirstOrDefault(),
				propertySymbol.ToDisplayString(),
				unsupportedReason));
			return;
		}

		var key = GetPropertyIdentityKey(propertySymbol);
		if (!properties.TryGetValue(key, out var existingRequirement))
		{
			properties.Add(key, new PropertyRequirement(
				propertySymbol,
				requiresOverride,
				requiresPublic,
				getterRequired ? propertySymbol.GetMethod : null,
				setterRequired ? propertySymbol.SetMethod : null));
			return;
		}

		if (!AreEquivalentPropertyShapes(existingRequirement.Symbol, propertySymbol))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				propertySymbol.Locations.FirstOrDefault(),
				propertySymbol.ToDisplayString(),
				"matching property signatures require different property types"));
			return;
		}

		var mergedRequiresOverride = existingRequirement.RequiresOverride || requiresOverride;
		var mergedRequiresPublic = existingRequirement.RequiresPublic || requiresPublic;
		var mergedGetterRequired = existingRequirement.Getter is not null || getterRequired;
		var mergedSetterRequired = existingRequirement.Setter is not null || setterRequired;

		var overrideProperty = existingRequirement.RequiresOverride
			? existingRequirement.Symbol
			: requiresOverride
				? propertySymbol
				: null;

		if (overrideProperty is not null)
		{
			if (mergedRequiresPublic && overrideProperty.DeclaredAccessibility != Accessibility.Public)
			{
				collection.Diagnostics.Add(Diagnostic.Create(
					ConflictingMemberDescriptor,
					overrideProperty.Locations.FirstOrDefault(),
					overrideProperty.ToDisplayString(),
					"a non-public abstract property cannot also satisfy a public interface contract implicitly"));
				return;
			}

			if (mergedGetterRequired && overrideProperty.GetMethod is null)
			{
				collection.Diagnostics.Add(Diagnostic.Create(
					ConflictingMemberDescriptor,
					overrideProperty.Locations.FirstOrDefault(),
					overrideProperty.ToDisplayString(),
					"an inherited property would need a getter that does not exist on the abstract base property"));
				return;
			}

			if (mergedSetterRequired && overrideProperty.SetMethod is null)
			{
				collection.Diagnostics.Add(Diagnostic.Create(
					ConflictingMemberDescriptor,
					overrideProperty.Locations.FirstOrDefault(),
					overrideProperty.ToDisplayString(),
					"an inherited property would need a setter that does not exist on the abstract base property"));
				return;
			}
		}

		if (existingRequirement.Setter is not null && setterRequired && propertySymbol.SetMethod is not null && existingRequirement.Setter.IsInitOnly != propertySymbol.SetMethod.IsInitOnly)
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				propertySymbol.Locations.FirstOrDefault(),
				propertySymbol.ToDisplayString(),
				"matching properties disagree on whether the setter is init-only"));
			return;
		}

		existingRequirement.RequiresOverride = mergedRequiresOverride;
		existingRequirement.RequiresPublic = mergedRequiresPublic;

		if (!existingRequirement.RequiresOverride && requiresOverride)
		{
			existingRequirement.Symbol = propertySymbol;
		}
		else if (!existingRequirement.RequiresOverride && HasMorePropertyAccessors(propertySymbol, existingRequirement.Symbol))
		{
			existingRequirement.Symbol = propertySymbol;
		}

		if (getterRequired)
		{
			existingRequirement.Getter = propertySymbol.GetMethod;
		}

		if (setterRequired)
		{
			existingRequirement.Setter = propertySymbol.SetMethod;
		}
	}

	private static void AddEventRequirement(
		MemberCollection collection,
		Dictionary<string, EventRequirement> events,
		IEventSymbol eventSymbol,
		bool requiresOverride,
		bool requiresPublic)
	{
		if (!IsSupportedEvent(eventSymbol, out var unsupportedReason))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				UnsupportedMemberDescriptor,
				eventSymbol.Locations.FirstOrDefault(),
				eventSymbol.ToDisplayString(),
				unsupportedReason));
			return;
		}

		var key = eventSymbol.Name;
		if (!events.TryGetValue(key, out var existingRequirement))
		{
			events.Add(key, new EventRequirement(eventSymbol, requiresOverride, requiresPublic));
			return;
		}

		if (!SymbolEqualityComparer.Default.Equals(existingRequirement.Symbol.Type, eventSymbol.Type))
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				eventSymbol.Locations.FirstOrDefault(),
				eventSymbol.ToDisplayString(),
				"matching event names require different delegate types"));
			return;
		}

		existingRequirement.RequiresPublic |= requiresPublic;
		if (!existingRequirement.RequiresOverride && requiresOverride)
		{
			existingRequirement.Symbol = eventSymbol;
			existingRequirement.RequiresOverride = true;
		}

		if (existingRequirement.RequiresOverride && existingRequirement.RequiresPublic && existingRequirement.Symbol.DeclaredAccessibility != Accessibility.Public)
		{
			collection.Diagnostics.Add(Diagnostic.Create(
				ConflictingMemberDescriptor,
				existingRequirement.Symbol.Locations.FirstOrDefault(),
				existingRequirement.Symbol.ToDisplayString(),
				"a non-public abstract event cannot also satisfy a public interface contract implicitly"));
		}
	}

	private static Dictionary<string, MethodPublicNames> CreateMethodPublicNameMap(IReadOnlyList<MethodRequirement> requirements, NameAllocator allocator)
	{
		var names = new Dictionary<string, MethodPublicNames>(StringComparer.Ordinal);
		foreach (var group in requirements.GroupBy(static requirement => requirement.Symbol.Name, StringComparer.Ordinal))
		{
			var methodName = group.Key;
			names.Add(
				methodName,
				new MethodPublicNames(
					OnMethodName: allocator.Reserve("On" + methodName),
					CallCountName: allocator.Reserve(methodName + "CallCount"),
					ReturnsName: group.Any(static requirement => CanGenerateMethodReturnHelper(requirement.Symbol))
						? allocator.Reserve(methodName + "Returns")
						: null,
					ReturnsResultName: group.Any(static requirement => CanGenerateWrappedResultHelper(requirement.Symbol.ReturnType))
						? allocator.Reserve(methodName + "ResultReturns")
						: null));
		}

		return names;
	}

	private static MethodPlan[] ApplyMethodEmissionRules(IReadOnlyList<MethodPlan> plans)
	{
		var emittedCallCounts = new HashSet<string>(StringComparer.Ordinal);
		var emittedReturnDefaults = new HashSet<string>(StringComparer.Ordinal);
		var emittedResultDefaults = new HashSet<string>(StringComparer.Ordinal);
		var result = new MethodPlan[plans.Count];

		for (var index = 0; index < plans.Count; index++)
		{
			var plan = plans[index];
			var emitCallCount = emittedCallCounts.Add(plan.CallCountName);
			var emitReturnsDefault = plan.ReturnsName is not null
				&& emittedReturnDefaults.Add(GetMethodReturnHelperSignatureKey(plan, plan.ReturnsName, plan.Symbol.ReturnType));
			var emitReturnsResultDefault = false;
			if (plan.ReturnsResultName is not null && TryGetWrappedResultType(plan.Symbol.ReturnType, out var wrappedResultType))
			{
				emitReturnsResultDefault = emittedResultDefaults.Add(GetMethodReturnHelperSignatureKey(plan, plan.ReturnsResultName, wrappedResultType!));
			}

			result[index] = plan with
			{
				EmitCallCount = emitCallCount,
				EmitReturnsDefaultHelper = emitReturnsDefault,
				EmitReturnsResultDefaultHelper = emitReturnsResultDefault
			};
		}

		return result;
	}

	private static string GetMethodReturnHelperSignatureKey(MethodPlan plan, string methodName, ITypeSymbol valueType)
	{
		var builder = new StringBuilder();
		builder.Append(methodName);
		builder.Append('`');
		builder.Append(plan.Symbol.TypeParameters.Length);
		builder.Append('|');
		builder.Append(GetTypeDisplay(valueType));
		return builder.ToString();
	}

	private static MethodPlan CreateMethodPlan(MethodRequirement requirement, NameAllocator allocator, MethodPublicNames publicNames, bool needsOverloadSuffix)
	{
		var methodSymbol = requirement.Symbol;
		var baseName = BuildMethodBaseName(methodSymbol, needsOverloadSuffix);
		var returnsByRef = methodSymbol.ReturnsByRef || methodSymbol.ReturnsByRefReadonly;
		var hasGenericRefReturn = returnsByRef && methodSymbol.TypeParameters.Length > 0;
		var customDelegateName = allocator.Reserve(baseName + "Handler");
		var customDelegateTypeName = customDelegateName + GetTypeParameterUsage(methodSymbol.TypeParameters);
		var canUseBuiltInHandler = CanUseBuiltInHandler(methodSymbol.Parameters, methodSymbol.ReturnsVoid ? null : methodSymbol.ReturnType, returnsByRef);
		var onMethodParameterType = canUseBuiltInHandler
			? GetBuiltInDelegateType(methodSymbol.Parameters, methodSymbol.ReturnsVoid ? null : methodSymbol.ReturnType)
			: customDelegateTypeName;

		return new MethodPlan(
			Symbol: methodSymbol,
			RequiresOverride: requirement.RequiresOverride,
			CustomDelegateName: customDelegateName,
			ExposeCustomDelegate: !canUseBuiltInHandler,
			OnMethodName: publicNames.OnMethodName,
			OnMethodParameterType: onMethodParameterType,
			HandlerFieldName: methodSymbol.TypeParameters.Length == 0 ? allocator.Reserve("_" + ToCamelCase(baseName) + "Handler") : null,
			HandlerMapName: methodSymbol.TypeParameters.Length > 0 ? allocator.Reserve("_" + ToCamelCase(baseName) + "Handlers") : null,
			HandlerKeyMethodName: methodSymbol.TypeParameters.Length > 0 ? allocator.Reserve("Get" + baseName + "HandlerKey") : null,
			CallCountName: publicNames.CallCountName,
			ReturnsName: CanGenerateMethodReturnHelper(methodSymbol) ? publicNames.ReturnsName : null,
			ReturnsResultName: CanGenerateWrappedResultHelper(methodSymbol.ReturnType) ? publicNames.ReturnsResultName : null,
			ParameterReturnKeyName: CanGenerateParameterReturnHelper(methodSymbol) ? allocator.Reserve(baseName + "ParameterReturnKey") : null,
			ParameterReturnMapName: CanGenerateParameterReturnHelper(methodSymbol) ? allocator.Reserve("_" + ToCamelCase(baseName) + "ParameterReturns") : null,
			ResultFieldName: returnsByRef && !hasGenericRefReturn ? allocator.Reserve("_" + ToCamelCase(baseName) + "Result") : null,
			ResultMapName: hasGenericRefReturn ? allocator.Reserve("_" + ToCamelCase(baseName) + "Results") : null,
			ResultHolderName: hasGenericRefReturn ? allocator.Reserve(baseName + "ResultHolder") : null,
			ResultHolderMethodName: hasGenericRefReturn ? allocator.Reserve("Get" + baseName + "ResultHolder") : null,
			EmitCallCount: true,
			EmitReturnsDefaultHelper: true,
			EmitReturnsResultDefaultHelper: true,
			UsesBuiltInHandler: canUseBuiltInHandler);
	}

	private static PropertyPlan CreatePropertyPlan(PropertyRequirement requirement, NameAllocator allocator, bool needsOverloadSuffix)
	{
		var propertySymbol = requirement.Symbol;
		var baseName = BuildPropertyBaseName(propertySymbol, needsOverloadSuffix);
		var getterParameters = propertySymbol.Parameters;

		var getterUsesBuiltInHandler = requirement.Getter is not null && CanUseBuiltInHandler(getterParameters, propertySymbol.Type, returnsByRef: false);
		var setterUsesBuiltInHandler = requirement.Setter is not null && CanUseBuiltInHandler(propertySymbol.Parameters, returnType: null, returnsByRef: false, trailingParameterType: propertySymbol.Type);
		var canStoreBackingValue = CanStoreBackingValue(propertySymbol);
		var hasBackingField = requirement.Getter is not null && !propertySymbol.IsIndexer && canStoreBackingValue;

		return new PropertyPlan(
			Symbol: propertySymbol,
			RequiresOverride: requirement.RequiresOverride,
			Getter: requirement.Getter,
			Setter: requirement.Setter,
			GetterDelegateName: requirement.Getter is not null ? allocator.Reserve(baseName + "GetHandler") : null,
			SetterDelegateName: requirement.Setter is not null ? allocator.Reserve(baseName + "SetHandler") : null,
			ExposeCustomGetterDelegate: requirement.Getter is not null && !getterUsesBuiltInHandler,
			ExposeCustomSetterDelegate: requirement.Setter is not null && !setterUsesBuiltInHandler,
			GetHandlerTypeName: requirement.Getter is null
				? null
				: getterUsesBuiltInHandler
					? GetBuiltInDelegateType(getterParameters, propertySymbol.Type)
					: allocator.Reserve("Get" + baseName),
			SetHandlerTypeName: requirement.Setter is null
				? null
				: setterUsesBuiltInHandler
					? GetBuiltInDelegateType(propertySymbol.Parameters, returnType: null, trailingParameterType: propertySymbol.Type)
					: allocator.Reserve("Set" + baseName),
			GetHandlerFieldName: requirement.Getter is not null ? allocator.Reserve("_" + ToCamelCase(baseName) + "GetHandler") : null,
			SetHandlerFieldName: requirement.Setter is not null ? allocator.Reserve("_" + ToCamelCase(baseName) + "SetHandler") : null,
			BackingFieldName: hasBackingField ? allocator.Reserve("_" + ToCamelCase(baseName) + "Value") : null,
			GetCallCountName: requirement.Getter is not null ? allocator.Reserve(baseName + "GetCallCount") : null,
			SetCallCountName: requirement.Setter is not null ? allocator.Reserve(baseName + "SetCallCount") : null,
			OnGetName: requirement.Getter is not null ? allocator.Reserve("OnGet" + baseName) : null,
			OnSetName: requirement.Setter is not null ? allocator.Reserve("OnSet" + baseName) : null,
			ReturnsName: CanGeneratePropertyReturnHelper(propertySymbol, hasBackingField, requirement.Getter is not null) ? allocator.Reserve(baseName + "Returns") : null,
			ReturnsResultName: CanGenerateWrappedResultHelper(propertySymbol.Type) && CanGeneratePropertyReturnHelper(propertySymbol, hasBackingField, requirement.Getter is not null)
				? allocator.Reserve(baseName + "ResultReturns")
				: null,
			GetterUsesBuiltInHandler: getterUsesBuiltInHandler,
			SetterUsesBuiltInHandler: setterUsesBuiltInHandler);
	}

	private static EventPlan CreateEventPlan(EventRequirement requirement, NameAllocator allocator)
	{
		var baseName = requirement.Symbol.Name;
		return new EventPlan(
			Symbol: requirement.Symbol,
			RequiresOverride: requirement.RequiresOverride,
			BackingFieldName: allocator.Reserve("_" + ToCamelCase(baseName)),
			AddCallCountName: allocator.Reserve(baseName + "AddCallCount"),
			RemoveCallCountName: allocator.Reserve(baseName + "RemoveCallCount"),
			RaiseMethodName: allocator.Reserve("Raise" + baseName));
	}

	private static string BuildSource(
		INamedTypeSymbol typeSymbol,
		TypeDeclarationSyntax declarationSyntax,
		IReadOnlyList<MethodPlan> methodPlans,
		IReadOnlyList<PropertyPlan> propertyPlans,
		IReadOnlyList<EventPlan> eventPlans,
		string? typeKeyHelperName)
	{
		using var builder = new SourceWriter();
		builder.AppendLine("#nullable enable");
		builder.AppendLine();

		var indentation = 0;
		if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
		{
			builder.Append("namespace ");
			builder.AppendLine(typeSymbol.ContainingNamespace.ToDisplayString());
			builder.AppendLine("{");
			builder.AppendLine();
			indentation++;
		}

		foreach (var containingType in GetContainingTypes(typeSymbol))
		{
			if (TryGetDeclarationSyntax(containingType) is not TypeDeclarationSyntax containingSyntax)
			{
				continue;
			}

			AppendTypeDeclaration(builder, containingType, containingSyntax, indentation);
			indentation++;
		}

		AppendTypeDeclaration(builder, typeSymbol, declarationSyntax, indentation);
		indentation++;

		var needsSpacer = false;
		foreach (var methodPlan in methodPlans)
		{
			if (needsSpacer)
			{
				builder.AppendLine();
			}

			AppendMethod(builder, methodPlan, methodPlans, indentation, typeKeyHelperName);
			needsSpacer = true;
		}

		foreach (var propertyPlan in propertyPlans)
		{
			if (needsSpacer)
			{
				builder.AppendLine();
			}

			AppendProperty(builder, propertyPlan, indentation);
			needsSpacer = true;
		}

		foreach (var eventPlan in eventPlans)
		{
			if (needsSpacer)
			{
				builder.AppendLine();
			}

			AppendEvent(builder, eventPlan, indentation);
			needsSpacer = true;
		}

		if (typeKeyHelperName is not null)
		{
			if (needsSpacer)
			{
				builder.AppendLine();
			}

			AppendTypeKeyHelper(builder, typeKeyHelperName, indentation);
			needsSpacer = true;
		}

		indentation--;
		AppendIndent(builder, indentation);
		builder.AppendLine("}");

		foreach (var _ in GetContainingTypes(typeSymbol))
		{
			indentation--;
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}

		if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
		{
			AppendIndent(builder, 0);
			builder.AppendLine();
			builder.AppendLine("}");
		}

		return builder.ToString();
	}

	private static void AppendMethod(SourceWriter builder, MethodPlan plan, IReadOnlyList<MethodPlan> methodPlans, int indentation, string? typeKeyHelperName)
	{
		var methodSymbol = plan.Symbol;
		AppendDelegate(builder, indentation, plan.CustomDelegateName, plan.ExposeCustomDelegate ? "public" : "private", methodSymbol.TypeParameters, methodSymbol.Parameters, methodSymbol.ReturnType, methodSymbol.ReturnsVoid, methodSymbol.ReturnsByRef, methodSymbol.ReturnsByRefReadonly);
		builder.AppendLine();

		if (plan.HandlerFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private ");
			builder.Append(plan.CustomDelegateName);
			builder.Append("? ");
			builder.Append(plan.HandlerFieldName);
			builder.AppendLine(";");
		}

		if (plan.HandlerMapName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private readonly global::System.Collections.Generic.Dictionary<global::System.String, global::System.Delegate> ");
			builder.Append(plan.HandlerMapName);
			builder.AppendLine(" = new();");
		}

		if (plan.ResultMapName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private readonly global::System.Collections.Generic.Dictionary<global::System.String, object> ");
			builder.Append(plan.ResultMapName);
			builder.AppendLine(" = new();");
		}

		if (plan.ParameterReturnMapName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private readonly global::System.Collections.Generic.Dictionary<");
			builder.Append(plan.ParameterReturnKeyName);
			builder.Append(", ");
			builder.Append(GetTypeDisplay(methodSymbol.ReturnType));
			builder.Append("> ");
			builder.Append(plan.ParameterReturnMapName);
			builder.AppendLine(" = new();");
		}

		if (plan.ResultFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private ");
			builder.Append(GetTypeDisplay(methodSymbol.ReturnType));
			builder.Append(' ');
			builder.Append(plan.ResultFieldName);
			builder.AppendLine(" = default!;");
		}

		if (plan.ResultHolderName is not null)
		{
			AppendGenericMethodResultHolder(builder, plan, indentation);
		}

		if (plan.ParameterReturnKeyName is not null)
		{
			AppendMethodParameterReturnKey(builder, plan, indentation);
			builder.AppendLine();
		}

		AppendIndent(builder, indentation);
		if (plan.EmitCallCount)
		{
			builder.Append("public int ");
			builder.Append(plan.CallCountName);
			builder.AppendLine(" { get; private set; }");
			builder.AppendLine();
		}

		AppendIndent(builder, indentation);
		builder.Append("public void ");
		builder.Append(plan.OnMethodName);
		AppendTypeParameters(builder, methodSymbol.TypeParameters);
		builder.Append('(');
		builder.Append(plan.OnMethodParameterType);
		builder.Append(" handler)");
		AppendConstraintLines(builder, methodSymbol.TypeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("if (handler is null)");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("throw new global::System.ArgumentNullException(nameof(handler));");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendOnMethodAssignment(builder, plan, indentation + 1);
		AppendIndent(builder, indentation);
		builder.AppendLine("}");

		if (plan.ReturnsName is not null)
		{
			builder.AppendLine();
			AppendMethodReturnHelper(builder, plan, methodPlans, indentation, plan.ReturnsName, methodSymbol.ReturnType, wrapInnerResult: false, emitDefaultHelper: plan.EmitReturnsDefaultHelper);
		}

		if (plan.ReturnsResultName is not null && TryGetWrappedResultType(methodSymbol.ReturnType, out var wrappedResultType))
		{
			builder.AppendLine();
			AppendMethodReturnHelper(builder, plan, methodPlans, indentation, plan.ReturnsResultName, wrappedResultType!, wrapInnerResult: true, emitDefaultHelper: plan.EmitReturnsResultDefaultHelper);
		}

		if (plan.ResultHolderMethodName is not null)
		{
			builder.AppendLine();
			AppendGenericMethodResultHolderAccessor(builder, plan, indentation);
		}

		if (plan.HandlerKeyMethodName is not null && typeKeyHelperName is not null)
		{
			builder.AppendLine();
			AppendHandlerKeyMethod(builder, plan.HandlerKeyMethodName, typeKeyHelperName, methodSymbol.TypeParameters, indentation);
		}

		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.Append(GetMemberAccessibility(plan.RequiresOverride ? methodSymbol.DeclaredAccessibility : Accessibility.Public));
		builder.Append(plan.RequiresOverride ? " override " : " ");
		AppendReturnType(builder, methodSymbol.ReturnType, methodSymbol.ReturnsVoid, methodSymbol.ReturnsByRef, methodSymbol.ReturnsByRefReadonly);
		builder.Append(' ');
		builder.Append(EscapeIdentifier(methodSymbol.Name));
		AppendTypeParameters(builder, methodSymbol.TypeParameters);
		builder.Append('(');
		AppendParameterList(builder, methodSymbol.Parameters, ParameterRenderingMode.MemberDeclaration);
		builder.Append(')');
		AppendConstraintLines(builder, methodSymbol.TypeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.Append(plan.CallCountName);
		builder.AppendLine("++;");
		builder.AppendLine();

		AppendMethodInvocation(builder, plan, indentation + 1);

		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendProperty(SourceWriter builder, PropertyPlan plan, int indentation)
	{
		if (plan.Getter is not null)
		{
			AppendDelegate(
				builder,
				indentation,
				plan.GetterDelegateName!,
				plan.ExposeCustomGetterDelegate ? "public" : "private",
				ImmutableArray<ITypeParameterSymbol>.Empty,
				plan.Symbol.Parameters,
				plan.Symbol.Type,
				returnsVoid: false,
				returnsByRef: false,
				returnsByRefReadonly: false);
			builder.AppendLine();
		}

		if (plan.Setter is not null)
		{
			AppendDelegate(
				builder,
				indentation,
				plan.SetterDelegateName!,
				plan.ExposeCustomSetterDelegate ? "public" : "private",
				ImmutableArray<ITypeParameterSymbol>.Empty,
				plan.Symbol.Parameters,
				returnType: null,
				returnsVoid: true,
				returnsByRef: false,
				returnsByRefReadonly: false,
				trailingParameterType: plan.Symbol.Type,
				trailingParameterName: AssignedValueParameterName);
			builder.AppendLine();
		}

		if (plan.GetHandlerFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private ");
			builder.Append(plan.GetterDelegateName);
			builder.Append("? ");
			builder.Append(plan.GetHandlerFieldName);
			builder.AppendLine(";");
		}

		if (plan.SetHandlerFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private ");
			builder.Append(plan.SetterDelegateName);
			builder.Append("? ");
			builder.Append(plan.SetHandlerFieldName);
			builder.AppendLine(";");
		}

		if (plan.BackingFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("private ");
			builder.Append(GetTypeDisplay(plan.Symbol.Type));
			builder.Append(' ');
			builder.Append(plan.BackingFieldName);
			builder.AppendLine(" = default!;");
		}

		if (plan.GetCallCountName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("public int ");
			builder.Append(plan.GetCallCountName);
			builder.AppendLine(" { get; private set; }");
		}

		if (plan.SetCallCountName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("public int ");
			builder.Append(plan.SetCallCountName);
			builder.AppendLine(" { get; private set; }");
		}

		if (plan.GetCallCountName is not null || plan.SetCallCountName is not null)
		{
			builder.AppendLine();
		}

		if (plan.OnGetName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("public void ");
			builder.Append(plan.OnGetName);
			builder.Append('(');
			builder.Append(plan.GetHandlerTypeName);
			builder.Append(" handler)");
			builder.AppendLine();
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("if (handler is null)");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 2);
			builder.AppendLine("throw new global::System.ArgumentNullException(nameof(handler));");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("}");
			builder.AppendLine();
			AppendOnPropertyGetterAssignment(builder, plan, indentation + 1);
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
			builder.AppendLine();
		}

		if (plan.OnSetName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("public void ");
			builder.Append(plan.OnSetName);
			builder.Append('(');
			builder.Append(plan.SetHandlerTypeName);
			builder.Append(" handler)");
			builder.AppendLine();
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("if (handler is null)");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 2);
			builder.AppendLine("throw new global::System.ArgumentNullException(nameof(handler));");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("}");
			builder.AppendLine();
			AppendOnPropertySetterAssignment(builder, plan, indentation + 1);
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
			builder.AppendLine();
		}

		if (plan.ReturnsName is not null)
		{
			AppendPropertyReturnHelper(builder, plan, indentation, plan.ReturnsName, plan.Symbol.Type, wrapInnerResult: false);
			builder.AppendLine();
		}

		if (plan.ReturnsResultName is not null && TryGetWrappedResultType(plan.Symbol.Type, out var wrappedResultType))
		{
			AppendPropertyReturnHelper(builder, plan, indentation, plan.ReturnsResultName, wrappedResultType!, wrapInnerResult: true);
			builder.AppendLine();
		}

		AppendIndent(builder, indentation);
		builder.Append(GetMemberAccessibility(plan.RequiresOverride ? plan.Symbol.DeclaredAccessibility : Accessibility.Public));
		builder.Append(plan.RequiresOverride ? " override " : " ");
		builder.Append(GetTypeDisplay(plan.Symbol.Type));
		builder.Append(' ');
		if (plan.Symbol.IsIndexer)
		{
			builder.Append("this[");
			AppendParameterList(builder, plan.Symbol.Parameters, ParameterRenderingMode.MemberDeclaration);
			builder.Append(']');
		}
		else
		{
			builder.Append(EscapeIdentifier(plan.Symbol.Name));
		}

		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");

		if (plan.Getter is not null || (plan.RequiresOverride && plan.Symbol.GetMethod is not null))
		{
			AppendAccessor(builder, plan, indentation + 1, isGetter: true);
		}

		if (plan.Setter is not null || (plan.RequiresOverride && plan.Symbol.SetMethod is not null))
		{
			AppendAccessor(builder, plan, indentation + 1, isGetter: false);
		}

		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendEvent(SourceWriter builder, EventPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private ");
		builder.Append(GetTypeDisplay(plan.Symbol.Type));
		builder.Append(' ');
		builder.Append(plan.BackingFieldName);
		builder.AppendLine(" = default!;");
		AppendIndent(builder, indentation);
		builder.Append("public int ");
		builder.Append(plan.AddCallCountName);
		builder.AppendLine(" { get; private set; }");
		AppendIndent(builder, indentation);
		builder.Append("public int ");
		builder.Append(plan.RemoveCallCountName);
		builder.AppendLine(" { get; private set; }");
		builder.AppendLine();

		AppendIndent(builder, indentation);
		builder.Append(GetMemberAccessibility(plan.RequiresOverride ? plan.Symbol.DeclaredAccessibility : Accessibility.Public));
		builder.Append(plan.RequiresOverride ? " override event " : " event ");
		builder.Append(GetTypeDisplay(plan.Symbol.Type));
		builder.Append(' ');
		builder.Append(EscapeIdentifier(plan.Symbol.Name));
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("add");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.Append(plan.AddCallCountName);
		builder.AppendLine("++;");
		AppendIndent(builder, indentation + 2);
		builder.Append(plan.BackingFieldName);
		builder.AppendLine(" += value;");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("remove");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.Append(plan.RemoveCallCountName);
		builder.AppendLine("++;");
		AppendIndent(builder, indentation + 2);
		builder.Append(plan.BackingFieldName);
		builder.AppendLine(" -= value;");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");

		if (plan.Symbol.Type is INamedTypeSymbol delegateType && delegateType.DelegateInvokeMethod is { } invokeMethod)
		{
			builder.AppendLine();
			AppendIndent(builder, indentation);
			builder.Append("public void ");
			builder.Append(plan.RaiseMethodName);
			builder.Append('(');
			AppendParameterList(builder, invokeMethod.Parameters, ParameterRenderingMode.MemberDeclaration);
			builder.AppendLine(")");
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			builder.Append("if (");
			builder.Append(plan.BackingFieldName);
			builder.AppendLine(" is null)");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 2);
			builder.AppendLine("return;");
			AppendIndent(builder, indentation + 1);
			builder.AppendLine("}");
			builder.AppendLine();
			AppendIndent(builder, indentation + 1);
			builder.Append(plan.BackingFieldName);
			builder.Append(".Invoke(");
			AppendArgumentList(builder, invokeMethod.Parameters);
			builder.AppendLine(");");
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}
	}

	private static void AppendAccessor(SourceWriter builder, PropertyPlan plan, int indentation, bool isGetter)
	{
		var accessorMethod = isGetter ? plan.Symbol.GetMethod : plan.Symbol.SetMethod;
		if (accessorMethod is null)
		{
			return;
		}

		var isRequired = isGetter ? plan.Getter is not null : plan.Setter is not null;
		var accessorAccessibility = GetAccessorAccessibility(accessorMethod, plan.Symbol.DeclaredAccessibility);
		AppendIndent(builder, indentation);
		if (accessorAccessibility.Length > 0)
		{
			builder.Append(accessorAccessibility);
			builder.Append(' ');
		}
		builder.Append(isGetter ? "get" : accessorMethod.IsInitOnly ? "init" : "set");
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");

		if (isGetter)
		{
			if (isRequired)
			{
				AppendIndent(builder, indentation + 1);
				builder.Append(plan.GetCallCountName);
				builder.AppendLine("++;");
				builder.AppendLine();
				AppendIndent(builder, indentation + 1);
				builder.Append("if (");
				builder.Append(plan.GetHandlerFieldName);
				builder.AppendLine(" is not null)");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("{");
				AppendIndent(builder, indentation + 2);
				builder.Append("return ");
				builder.Append(plan.GetHandlerFieldName);
				builder.Append('(');
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("}");
				builder.AppendLine();

				AppendIndent(builder, indentation + 1);
				builder.Append("return ");
				if (plan.BackingFieldName is not null)
				{
					builder.Append(plan.BackingFieldName);
				}
				else
				{
					builder.Append(GetDefaultReturnExpression(plan.Symbol.Type));
				}
				builder.AppendLine(";");
			}
			else
			{
				AppendIndent(builder, indentation + 1);
				builder.Append("return base.");
				AppendBasePropertyReference(builder, plan.Symbol);
				builder.AppendLine(";");
			}
		}
		else
		{
			if (isRequired)
			{
				AppendIndent(builder, indentation + 1);
				builder.Append(plan.SetCallCountName);
				builder.AppendLine("++;");
				builder.AppendLine();

				AppendIndent(builder, indentation + 1);
				builder.Append("if (");
				builder.Append(plan.SetHandlerFieldName);
				builder.AppendLine(" is not null)");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("{");
				AppendIndent(builder, indentation + 2);
				builder.Append(plan.SetHandlerFieldName);
				builder.Append('(');
				AppendArgumentList(builder, plan.Symbol.Parameters);
				if (plan.Symbol.Parameters.Length > 0)
				{
					builder.Append(", ");
				}
				builder.AppendLine("value);");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("}");

				if (plan.BackingFieldName is not null)
				{
					builder.AppendLine();
					AppendIndent(builder, indentation + 1);
					builder.Append(plan.BackingFieldName);
					builder.AppendLine(" = value;");
				}
			}
			else
			{
				AppendIndent(builder, indentation + 1);
				builder.Append("base.");
				AppendBasePropertyReference(builder, plan.Symbol);
				builder.AppendLine(" = value;");
			}
		}

		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendMethodInvocation(SourceWriter builder, MethodPlan plan, int indentation)
	{
		if (plan.ParameterReturnMapName is not null)
		{
			var returnValueLocalName = GetParameterReturnValueLocalName(plan.Symbol.Parameters);

			AppendIndent(builder, indentation);
			builder.Append("if (");
			builder.Append(plan.ParameterReturnMapName);
			builder.Append(".TryGetValue(new ");
			builder.Append(plan.ParameterReturnKeyName);
			builder.Append('(');
			AppendParameterKeyArgumentList(builder, plan.Symbol.Parameters);
			builder.Append("), out var ");
			builder.Append(returnValueLocalName);
			builder.AppendLine("))");
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			builder.Append("return ");
			builder.Append(returnValueLocalName);
			builder.AppendLine(";");
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}

		if (plan.HandlerFieldName is not null)
		{
			if (plan.ParameterReturnMapName is not null)
			{
				builder.AppendLine();
			}

			AppendIndent(builder, indentation);
			builder.Append("if (");
			builder.Append(plan.HandlerFieldName);
			builder.AppendLine(" is not null)");
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			if (plan.Symbol.ReturnsVoid)
			{
				builder.Append(plan.HandlerFieldName);
				builder.Append('(');
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("return;");
			}
			else if (plan.Symbol.ReturnsByRef || plan.Symbol.ReturnsByRefReadonly)
			{
				builder.Append("return ref ");
				builder.Append(plan.HandlerFieldName);
				builder.Append('(');
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
			}
			else
			{
				builder.Append("return ");
				builder.Append(plan.HandlerFieldName);
				builder.Append('(');
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
			}
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}
		else if (plan.HandlerMapName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append("if (");
			builder.Append(plan.HandlerMapName);
			builder.Append(".TryGetValue(");
			builder.Append(plan.HandlerKeyMethodName);
			AppendTypeParameters(builder, plan.Symbol.TypeParameters);
			builder.AppendLine("(), out var handler))");
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			AppendIndent(builder, indentation + 1);
			if (plan.Symbol.ReturnsVoid)
			{
				builder.Append("((");
				builder.Append(plan.CustomDelegateName);
				AppendTypeParameters(builder, plan.Symbol.TypeParameters);
				builder.Append(")handler)(");
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
				AppendIndent(builder, indentation + 1);
				builder.AppendLine("return;");
			}
			else
			{
				builder.Append(plan.Symbol.ReturnsByRef || plan.Symbol.ReturnsByRefReadonly ? "return ref ((" : "return ((");
				builder.Append(plan.CustomDelegateName);
				AppendTypeParameters(builder, plan.Symbol.TypeParameters);
				builder.Append(")handler)(");
				AppendArgumentList(builder, plan.Symbol.Parameters);
				builder.AppendLine(");");
			}
			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}

		if (plan.Symbol.Parameters.Any(static parameter => parameter.RefKind == RefKind.Out))
		{
			if (plan.HandlerFieldName is not null || plan.HandlerMapName is not null)
			{
				builder.AppendLine();
			}

			foreach (var outParameter in plan.Symbol.Parameters.Where(static parameter => parameter.RefKind == RefKind.Out))
			{
				AppendIndent(builder, indentation);
				builder.Append(EscapeIdentifier(outParameter.Name));
				builder.AppendLine(" = default!;");
			}
		}

		if (plan.Symbol.ReturnsVoid)
		{
			return;
		}

		if (plan.Symbol.ReturnsByRef || plan.Symbol.ReturnsByRefReadonly)
		{
			if (plan.Symbol.Parameters.Any(static parameter => parameter.RefKind == RefKind.Out))
			{
				builder.AppendLine();
			}

			AppendIndent(builder, indentation);
			builder.Append("return ref ");
			AppendMethodRefResultReference(builder, plan);
			builder.AppendLine(";");
			return;
		}

		if (plan.Symbol.Parameters.Any(static parameter => parameter.RefKind == RefKind.Out))
		{
			builder.AppendLine();
		}

		AppendIndent(builder, indentation);
		builder.Append("return ");
		builder.Append(GetDefaultReturnExpression(plan.Symbol.ReturnType));
		builder.AppendLine(";");
	}

	private static void AppendMethodReturnHelper(
		SourceWriter builder,
		MethodPlan plan,
		IReadOnlyList<MethodPlan> methodPlans,
		int indentation,
		string methodName,
		ITypeSymbol valueType,
		bool wrapInnerResult,
		bool emitDefaultHelper)
	{
		var valueParameterName = GetReturnValueParameterName(plan.Symbol.Parameters);

		if (emitDefaultHelper)
		{
			AppendIndent(builder, indentation);
			builder.Append("public void ");
			builder.Append(methodName);
			AppendTypeParameters(builder, plan.Symbol.TypeParameters);
			builder.Append('(');
			builder.Append(GetTypeDisplay(valueType));
			builder.Append(' ');
			builder.Append(valueParameterName);
			builder.Append(')');
			AppendConstraintLines(builder, plan.Symbol.TypeParameters, indentation);
			builder.AppendLine();
			AppendIndent(builder, indentation);
			builder.AppendLine("{");
			foreach (var assignmentPlan in GetMethodReturnAssignmentPlans(plan, methodPlans, methodName, valueType, wrapInnerResult))
			{
				AppendMethodReturnAssignment(builder, assignmentPlan, indentation + 1, wrapInnerResult ? GetWrappedReturnExpression(assignmentPlan.Symbol.ReturnType, valueParameterName) : valueParameterName);
			}

			AppendIndent(builder, indentation);
			builder.AppendLine("}");
		}

		if (plan.ParameterReturnMapName is null)
		{
			return;
		}

		if (emitDefaultHelper)
		{
			builder.AppendLine();
		}

		AppendIndent(builder, indentation);
		builder.Append("public void ");
		builder.Append(methodName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append('(');
		AppendParameterList(builder, plan.Symbol.Parameters, ParameterRenderingMode.MemberDeclaration, valueType, valueParameterName);
		builder.Append(')');
		AppendConstraintLines(builder, plan.Symbol.TypeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.Append(plan.ParameterReturnMapName);
		builder.Append("[new ");
		builder.Append(plan.ParameterReturnKeyName);
		builder.Append('(');
		AppendParameterKeyArgumentList(builder, plan.Symbol.Parameters);
		builder.Append(")] = ");
		builder.Append(wrapInnerResult ? GetWrappedReturnExpression(plan.Symbol.ReturnType, valueParameterName) : valueParameterName);
		builder.AppendLine(";");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static IEnumerable<MethodPlan> GetMethodReturnAssignmentPlans(MethodPlan plan, IEnumerable<MethodPlan> methodPlans, string methodName, ITypeSymbol valueType, bool wrapInnerResult)
	{
		if (plan.Symbol.TypeParameters.Length > 0)
		{
			yield return plan;
			yield break;
		}

		foreach (var candidate in methodPlans)
		{
			if (candidate.Symbol.TypeParameters.Length > 0)
			{
				continue;
			}

			if (wrapInnerResult)
			{
				if (candidate.ReturnsResultName == methodName
					&& TryGetWrappedResultType(candidate.Symbol.ReturnType, out var candidateValueType)
					&& SymbolEqualityComparer.Default.Equals(candidateValueType, valueType))
				{
					yield return candidate;
				}
			}
			else if (candidate.ReturnsName == methodName && SymbolEqualityComparer.Default.Equals(candidate.Symbol.ReturnType, valueType))
			{
				yield return candidate;
			}
		}
	}

	private static void AppendMethodReturnAssignment(SourceWriter builder, MethodPlan plan, int indentation, string returnExpression)
	{
		if (plan.Symbol.ReturnsByRef || plan.Symbol.ReturnsByRefReadonly)
		{
			AppendIndent(builder, indentation);
			AppendMethodRefResultReference(builder, plan);
			builder.Append(" = ");
			builder.Append(returnExpression);
			builder.AppendLine(";");

			if (plan.HandlerFieldName is not null)
			{
				AppendIndent(builder, indentation);
				builder.Append(plan.HandlerFieldName);
				builder.AppendLine(" = null;");
			}
			else if (plan.HandlerMapName is not null)
			{
				AppendIndent(builder, indentation);
				builder.Append(plan.HandlerMapName);
				builder.Append(".Remove(");
				builder.Append(plan.HandlerKeyMethodName);
				AppendTypeParameters(builder, plan.Symbol.TypeParameters);
				builder.AppendLine("());");
			}

			return;
		}

		if (plan.HandlerFieldName is not null)
		{
			AppendIndent(builder, indentation);
			builder.Append(plan.HandlerFieldName);
			builder.Append(" = ");
			AppendConstantReturnDelegate(builder, indentation, plan.CustomDelegateName, plan.Symbol.TypeParameters, plan.Symbol.Parameters, plan.Symbol.ReturnType, returnExpression);
			builder.AppendLine(";");
			return;
		}

		AppendIndent(builder, indentation);
		builder.Append(plan.HandlerMapName);
		builder.Append('[');
		builder.Append(plan.HandlerKeyMethodName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append("] = ");
		builder.Append('(');
		builder.Append(plan.CustomDelegateName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append(')');
		AppendConstantReturnDelegate(builder, indentation, plan.CustomDelegateName, plan.Symbol.TypeParameters, plan.Symbol.Parameters, plan.Symbol.ReturnType, returnExpression);
		builder.AppendLine(";");
	}

	private static void AppendMethodParameterReturnKey(SourceWriter builder, MethodPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private readonly struct ");
		builder.Append(plan.ParameterReturnKeyName);
		builder.Append(" : global::System.IEquatable<");
		builder.Append(plan.ParameterReturnKeyName);
		builder.AppendLine(">");
		AppendIndent(builder, indentation);
		builder.AppendLine("{");

		for (var index = 0; index < plan.Symbol.Parameters.Length; index++)
		{
			AppendIndent(builder, indentation + 1);
			builder.Append("private readonly ");
			builder.Append(GetTypeDisplay(plan.Symbol.Parameters[index].Type));
			builder.Append(" _parameter");
			builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.AppendLine(";");
		}

		builder.AppendLine();
		AppendIndent(builder, indentation + 1);
		builder.Append("public ");
		builder.Append(plan.ParameterReturnKeyName);
		builder.Append('(');
		AppendParameterList(builder, plan.Symbol.Parameters, ParameterRenderingMode.MemberDeclaration);
		builder.AppendLine(")");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		for (var index = 0; index < plan.Symbol.Parameters.Length; index++)
		{
			AppendIndent(builder, indentation + 2);
			builder.Append("_parameter");
			builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.Append(" = ");
			builder.Append(EscapeIdentifier(plan.Symbol.Parameters[index].Name));
			builder.AppendLine(";");
		}
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();

		AppendIndent(builder, indentation + 1);
		builder.Append("public bool Equals(");
		builder.Append(plan.ParameterReturnKeyName);
		builder.AppendLine(" other)");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.Append("return ");
		for (var index = 0; index < plan.Symbol.Parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.AppendLine();
				AppendIndent(builder, indentation + 3);
				builder.Append("&& ");
			}

			builder.Append("global::System.Collections.Generic.EqualityComparer<");
			builder.Append(GetTypeDisplay(plan.Symbol.Parameters[index].Type));
			builder.Append(">.Default.Equals(_parameter");
			builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.Append(", other._parameter");
			builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.Append(')');
		}
		builder.AppendLine(";");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();

		AppendIndent(builder, indentation + 1);
		builder.AppendLine("public override bool Equals(object? obj)");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.Append("return obj is ");
		builder.Append(plan.ParameterReturnKeyName);
		builder.AppendLine(" other && Equals(other);");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();

		AppendIndent(builder, indentation + 1);
		builder.AppendLine("public override int GetHashCode()");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("unchecked");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 3);
		builder.AppendLine("var hashCode = 17;");
		for (var index = 0; index < plan.Symbol.Parameters.Length; index++)
		{
			AppendIndent(builder, indentation + 3);
			builder.Append("hashCode = (hashCode * 31) + global::System.Collections.Generic.EqualityComparer<");
			builder.Append(GetTypeDisplay(plan.Symbol.Parameters[index].Type));
			builder.Append(">.Default.GetHashCode(_parameter");
			builder.Append(index.ToString(System.Globalization.CultureInfo.InvariantCulture));
			builder.AppendLine("!);");
		}
		AppendIndent(builder, indentation + 3);
		builder.AppendLine("return hashCode;");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("}");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendOnMethodAssignment(SourceWriter builder, MethodPlan plan, int indentation)
	{
		if (!plan.UsesBuiltInHandler)
		{
			AppendIndent(builder, indentation);
			if (plan.HandlerFieldName is not null)
			{
				builder.Append(plan.HandlerFieldName);
				builder.AppendLine(" = handler;");
			}
			else
			{
				builder.Append(plan.HandlerMapName);
				builder.Append('[');
				builder.Append(plan.HandlerKeyMethodName);
				AppendTypeParameters(builder, plan.Symbol.TypeParameters);
				builder.AppendLine("()] = handler;");
			}
			return;
		}

		var wrapper = BuildBuiltInDelegateWrapper(plan.Symbol.Parameters, "handler");

		AppendIndent(builder, indentation);
		if (plan.HandlerFieldName is not null)
		{
			builder.Append(plan.HandlerFieldName);
			builder.Append(" = ");
			builder.Append(wrapper);
			builder.AppendLine(";");
		}
		else
		{
			builder.Append(plan.HandlerMapName);
			builder.Append('[');
			builder.Append(plan.HandlerKeyMethodName);
			AppendTypeParameters(builder, plan.Symbol.TypeParameters);
			builder.Append("] = (");
			builder.Append(plan.CustomDelegateName);
			AppendTypeParameters(builder, plan.Symbol.TypeParameters);
			builder.Append(")");
			builder.Append(wrapper);
			builder.AppendLine(";");
		}
	}

	private static void AppendPropertyReturnHelper(SourceWriter builder, PropertyPlan plan, int indentation, string methodName, ITypeSymbol valueType, bool wrapInnerResult)
	{
		AppendIndent(builder, indentation);
		builder.Append("public void ");
		builder.Append(methodName);
		builder.Append('(');
		builder.Append(GetTypeDisplay(valueType));
		builder.Append(" value)");
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		var returnExpression = wrapInnerResult ? GetWrappedReturnExpression(plan.Symbol.Type, "value") : "value";

		if (plan.BackingFieldName is not null)
		{
			AppendIndent(builder, indentation + 1);
			builder.Append(plan.BackingFieldName);
			builder.Append(" = ");
			builder.Append(returnExpression);
			builder.AppendLine(";");
			AppendIndent(builder, indentation + 1);
			builder.Append(plan.GetHandlerFieldName);
			builder.AppendLine(" = null;");
		}
		else
		{
			AppendIndent(builder, indentation + 1);
			builder.Append(plan.GetHandlerFieldName);
			builder.Append(" = ");
			AppendConstantReturnDelegate(builder, indentation + 1, plan.GetterDelegateName!, ImmutableArray<ITypeParameterSymbol>.Empty, plan.Symbol.Parameters, plan.Symbol.Type, returnExpression);
			builder.AppendLine(";");
		}

		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendOnPropertyGetterAssignment(SourceWriter builder, PropertyPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append(plan.GetHandlerFieldName);
		builder.Append(" = ");
		builder.Append(plan.GetterUsesBuiltInHandler
			? BuildBuiltInDelegateWrapper(plan.Symbol.Parameters, "handler")
			: "handler");
		builder.AppendLine(";");
	}

	private static void AppendOnPropertySetterAssignment(SourceWriter builder, PropertyPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append(plan.SetHandlerFieldName);
		builder.Append(" = ");
		builder.Append(plan.SetterUsesBuiltInHandler
			? BuildBuiltInDelegateWrapper(plan.Symbol.Parameters, handlerName: "handler", trailingParameterName: AssignedValueParameterName)
			: "handler");
		builder.AppendLine(";");
	}

	private static void AppendGenericMethodResultHolder(SourceWriter builder, MethodPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private sealed class ");
		builder.Append(plan.ResultHolderName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		AppendConstraintLines(builder, plan.Symbol.TypeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.Append("public ");
		builder.Append(GetTypeDisplay(plan.Symbol.ReturnType));
		builder.AppendLine(" Value = default!;");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
		builder.AppendLine();
	}

	private static void AppendGenericMethodResultHolderAccessor(SourceWriter builder, MethodPlan plan, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private ");
		builder.Append(plan.ResultHolderName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append(' ');
		builder.Append(plan.ResultHolderMethodName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append("()");
		AppendConstraintLines(builder, plan.Symbol.TypeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.Append("var key = ");
		builder.Append(plan.HandlerKeyMethodName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.AppendLine("();");
		AppendIndent(builder, indentation + 1);
		builder.Append("if (!");
		builder.Append(plan.ResultMapName);
		builder.AppendLine(".TryGetValue(key, out var holder))");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.Append("holder = new ");
		builder.Append(plan.ResultHolderName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.AppendLine("();");
		AppendIndent(builder, indentation + 2);
		builder.Append(plan.ResultMapName);
		builder.AppendLine("[key] = holder;");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendIndent(builder, indentation + 1);
		builder.Append("return (");
		builder.Append(plan.ResultHolderName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.AppendLine(")holder;");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendMethodRefResultReference(SourceWriter builder, MethodPlan plan)
	{
		if (plan.ResultFieldName is not null)
		{
			builder.Append(plan.ResultFieldName);
			return;
		}

		builder.Append(plan.ResultHolderMethodName);
		AppendTypeParameters(builder, plan.Symbol.TypeParameters);
		builder.Append("().Value");
	}

	private static void AppendTypeKeyHelper(SourceWriter builder, string helperName, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private static global::System.String ");
		builder.Append(helperName);
		builder.AppendLine("(params global::System.Type[] types)");
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("if (types.Length == 0)");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("return global::System.String.Empty;");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("var builder = new global::System.Text.StringBuilder();");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("for (var index = 0; index < types.Length; index++)");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("if (index > 0)");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 3);
		builder.AppendLine("builder.Append('|');");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("var current = types[index];");
		AppendIndent(builder, indentation + 2);
		builder.AppendLine("builder.Append(current.AssemblyQualifiedName ?? current.FullName ?? current.Name);");
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("}");
		builder.AppendLine();
		AppendIndent(builder, indentation + 1);
		builder.AppendLine("return builder.ToString();");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendHandlerKeyMethod(SourceWriter builder, string methodName, string typeKeyHelperName, ImmutableArray<ITypeParameterSymbol> typeParameters, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append("private static global::System.String ");
		builder.Append(methodName);
		AppendTypeParameters(builder, typeParameters);
		builder.Append("()");
		AppendConstraintLines(builder, typeParameters, indentation);
		builder.AppendLine();
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
		AppendIndent(builder, indentation + 1);
		builder.Append("return ");
		builder.Append(typeKeyHelperName);
		builder.Append('(');
		for (var index = 0; index < typeParameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			builder.Append("typeof(");
			builder.Append(typeParameters[index].Name);
			builder.Append(')');
		}
		builder.AppendLine(");");
		AppendIndent(builder, indentation);
		builder.AppendLine("}");
	}

	private static void AppendTypeDeclaration(SourceWriter builder, INamedTypeSymbol symbol, TypeDeclarationSyntax syntax, int indentation)
	{
		AppendIndent(builder, indentation);
		builder.Append(GetTypeModifiers(syntax));
		builder.Append(GetTypeKeyword(syntax));
		builder.Append(' ');
		builder.Append(EscapeIdentifier(symbol.Name));
		AppendTypeParameters(builder, symbol.TypeParameters);
		AppendTypeConstraintLines(builder, symbol.TypeParameters, indentation);
		AppendIndent(builder, indentation);
		builder.AppendLine("{");
	}

	private static void AppendDelegate(
		SourceWriter builder,
		int indentation,
		string delegateName,
		string accessibility,
		ImmutableArray<ITypeParameterSymbol> typeParameters,
		ImmutableArray<IParameterSymbol> parameters,
		ITypeSymbol? returnType,
		bool returnsVoid,
		bool returnsByRef,
		bool returnsByRefReadonly,
		ITypeSymbol? trailingParameterType = null,
		string? trailingParameterName = null)
	{
		AppendIndent(builder, indentation);
		builder.Append(accessibility);
		builder.Append(" delegate ");
		AppendReturnType(builder, returnType, returnsVoid, returnsByRef, returnsByRefReadonly);
		builder.Append(' ');
		builder.Append(delegateName);
		AppendTypeParameters(builder, typeParameters);
		builder.Append('(');
		AppendParameterList(builder, parameters, ParameterRenderingMode.DelegateDeclaration, trailingParameterType, trailingParameterName);
		builder.Append(')');

		var constraints = BuildConstraintClauses(typeParameters);
		if (constraints.Count == 0)
		{
			builder.AppendLine(";");
			return;
		}

		builder.AppendLine();
		for (var index = 0; index < constraints.Count; index++)
		{
			AppendIndent(builder, indentation + 1);
			builder.Append("where ");
			builder.Append(constraints[index]);
			builder.AppendLine(index == constraints.Count - 1 ? ";" : string.Empty);
		}
	}

	private static void AppendReturnType(SourceWriter builder, ITypeSymbol? returnType, bool returnsVoid, bool returnsByRef, bool returnsByRefReadonly)
	{
		if (returnsByRefReadonly)
		{
			builder.Append("ref readonly ");
		}
		else if (returnsByRef)
		{
			builder.Append("ref ");
		}

		builder.Append(returnsVoid ? "void" : GetTypeDisplay(returnType!));
	}

	private static void AppendTypeParameters(SourceWriter builder, ImmutableArray<ITypeParameterSymbol> typeParameters)
	{
		if (typeParameters.Length == 0)
		{
			return;
		}

		builder.Append('<');
		for (var index = 0; index < typeParameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			builder.Append(typeParameters[index].Name);
		}
		builder.Append('>');
	}

	private static string GetTypeParameterUsage(ImmutableArray<ITypeParameterSymbol> typeParameters)
	{
		return typeParameters.Length == 0
			? string.Empty
			: "<" + string.Join(", ", typeParameters.Select(static typeParameter => typeParameter.Name)) + ">";
	}

	private static void AppendTypeConstraintLines(SourceWriter builder, ImmutableArray<ITypeParameterSymbol> typeParameters, int indentation)
	{
		var constraints = BuildConstraintClauses(typeParameters);
		if (constraints.Count == 0)
		{
			builder.AppendLine();
			return;
		}

		builder.AppendLine();
		foreach (var constraint in constraints)
		{
			AppendIndent(builder, indentation + 1);
			builder.Append("where ");
			builder.AppendLine(constraint);
		}
	}

	private static void AppendConstraintLines(SourceWriter builder, ImmutableArray<ITypeParameterSymbol> typeParameters, int indentation)
	{
		var constraints = BuildConstraintClauses(typeParameters);
		if (constraints.Count == 0)
		{
			return;
		}

		builder.AppendLine();
		foreach (var constraint in constraints)
		{
			AppendIndent(builder, indentation + 1);
			builder.Append("where ");
			builder.Append(constraint);
			builder.AppendLine();
		}
	}

	private static List<string> BuildConstraintClauses(ImmutableArray<ITypeParameterSymbol> typeParameters)
	{
		var constraints = new List<string>();

		foreach (var typeParameter in typeParameters)
		{
			var parts = new List<string>();

			if (typeParameter.HasUnmanagedTypeConstraint)
			{
				parts.Add("unmanaged");
			}
			else if (typeParameter.HasValueTypeConstraint)
			{
				parts.Add("struct");
			}
			else if (typeParameter.HasReferenceTypeConstraint)
			{
				parts.Add(typeParameter.ReferenceTypeConstraintNullableAnnotation == NullableAnnotation.Annotated ? "class?" : "class");
			}

			if (typeParameter.HasNotNullConstraint)
			{
				parts.Add("notnull");
			}

			parts.AddRange(typeParameter.ConstraintTypes.Select(GetTypeDisplay));

			if (typeParameter.HasConstructorConstraint)
			{
				parts.Add("new()");
			}

			if (parts.Count > 0)
			{
				constraints.Add(typeParameter.Name + " : " + string.Join(", ", parts));
			}
		}

		return constraints;
	}

	private static void AppendConstantReturnDelegate(
		SourceWriter builder,
		int indentation,
		string delegateName,
		ImmutableArray<ITypeParameterSymbol> typeParameters,
		ImmutableArray<IParameterSymbol> parameters,
		ITypeSymbol returnType,
		string returnExpression)
	{
		builder.Append("delegate(");
		AppendParameterList(builder, parameters, ParameterRenderingMode.AnonymousMethod);
		builder.AppendLine(")");
		AppendIndent(builder, indentation);
		builder.AppendLine("{");

		foreach (var outParameter in parameters.Where(static parameter => parameter.RefKind == RefKind.Out))
		{
			AppendIndent(builder, indentation + 1);
			builder.Append(EscapeIdentifier(outParameter.Name));
			builder.AppendLine(" = default!;");
		}

		if (parameters.Any(static parameter => parameter.RefKind == RefKind.Out))
		{
			builder.AppendLine();
		}

		AppendIndent(builder, indentation + 1);
		builder.Append("return ");
		builder.Append(returnExpression);
		builder.AppendLine(";");
		AppendIndent(builder, indentation);
		builder.Append("}");
	}

	private static string BuildBuiltInDelegateWrapper(ImmutableArray<IParameterSymbol> parameters, string handlerName, string? trailingParameterName = null)
	{
		var builder = new StringBuilder();
		builder.Append('(');
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			builder.Append(EscapeIdentifier(parameters[index].Name));
		}

		if (!string.IsNullOrEmpty(trailingParameterName))
		{
			if (parameters.Length > 0)
			{
				builder.Append(", ");
			}

			builder.Append(EscapeIdentifier(trailingParameterName!));
		}

		builder.Append(") => ");
		builder.Append(handlerName);
		builder.Append('(');
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			builder.Append(EscapeIdentifier(parameters[index].Name));
		}

		if (!string.IsNullOrEmpty(trailingParameterName))
		{
			if (parameters.Length > 0)
			{
				builder.Append(", ");
			}

			builder.Append(EscapeIdentifier(trailingParameterName!));
		}

		builder.Append(')');
		return builder.ToString();
	}

	private static void AppendParameterList(
		SourceWriter builder,
		ImmutableArray<IParameterSymbol> parameters,
		ParameterRenderingMode mode,
		ITypeSymbol? trailingParameterType = null,
		string? trailingParameterName = null)
	{
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			AppendParameter(builder, parameters[index], mode);
		}

		if (trailingParameterType is not null && !string.IsNullOrWhiteSpace(trailingParameterName))
		{
			if (parameters.Length > 0)
			{
				builder.Append(", ");
			}

			if (mode != ParameterRenderingMode.ArgumentList)
			{
				builder.Append(GetTypeDisplay(trailingParameterType));
				builder.Append(' ');
			}

			builder.Append(EscapeIdentifier(trailingParameterName!));
		}
	}

	private static void AppendParameter(SourceWriter builder, IParameterSymbol parameter, ParameterRenderingMode mode)
	{
		if (mode is ParameterRenderingMode.MemberDeclaration or ParameterRenderingMode.DelegateDeclaration)
		{
			if (parameter.IsParams)
			{
				builder.Append("params ");
			}
		}

		var modifier = mode == ParameterRenderingMode.ArgumentList
			? GetArgumentModifier(parameter.RefKind)
			: GetParameterModifier(parameter.RefKind);
		if (modifier.Length > 0)
		{
			builder.Append(modifier);
			builder.Append(' ');
		}

		if (mode != ParameterRenderingMode.ArgumentList)
		{
			builder.Append(GetTypeDisplay(parameter.Type));
			builder.Append(' ');
		}

		builder.Append(EscapeIdentifier(parameter.Name));
	}

	private static void AppendArgumentList(SourceWriter builder, ImmutableArray<IParameterSymbol> parameters)
	{
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			AppendParameter(builder, parameters[index], ParameterRenderingMode.ArgumentList);
		}
	}

	private static void AppendParameterKeyArgumentList(SourceWriter builder, ImmutableArray<IParameterSymbol> parameters)
	{
		for (var index = 0; index < parameters.Length; index++)
		{
			if (index > 0)
			{
				builder.Append(", ");
			}

			builder.Append(EscapeIdentifier(parameters[index].Name));
		}
	}

	private static string GetReturnValueParameterName(ImmutableArray<IParameterSymbol> parameters)
	{
		return GetUniqueName(parameters, "value", "returnValue", "result");
	}

	private static string GetParameterReturnValueLocalName(ImmutableArray<IParameterSymbol> parameters)
	{
		return GetUniqueName(parameters, "parameterReturnValue", "configuredReturnValue", "matchedReturnValue");
	}

	private static string GetUniqueName(ImmutableArray<IParameterSymbol> parameters, params string[] candidates)
	{
		var names = new HashSet<string>(parameters.Select(static parameter => parameter.Name), StringComparer.Ordinal);
		foreach (var candidate in candidates)
		{
			if (names.Add(candidate))
			{
				return candidate;
			}
		}

		var index = 0;
		while (true)
		{
			var candidate = "generatedValue" + index.ToString(System.Globalization.CultureInfo.InvariantCulture);
			if (names.Add(candidate))
			{
				return candidate;
			}

			index++;
		}
	}

	private static void AppendBasePropertyReference(SourceWriter builder, IPropertySymbol propertySymbol)
	{
		if (propertySymbol.IsIndexer)
		{
			builder.Append("this[");
			AppendArgumentList(builder, propertySymbol.Parameters);
			builder.Append(']');
			return;
		}

		builder.Append(EscapeIdentifier(propertySymbol.Name));
	}

	private static bool HasFakeAttribute(INamedTypeSymbol typeSymbol, INamedTypeSymbol fakeAttributeSymbol)
	{
		foreach (var attribute in typeSymbol.GetAttributes())
		{
			if (SymbolEqualityComparer.Default.Equals(attribute.AttributeClass, fakeAttributeSymbol))
			{
				return true;
			}
		}

		return false;
	}

	private static string BuildHintName(INamedTypeSymbol typeSymbol)
	{
		var builder = new StringBuilder();

		if (!typeSymbol.ContainingNamespace.IsGlobalNamespace)
		{
			builder.Append(typeSymbol.ContainingNamespace.ToDisplayString());
			builder.Append('.');
		}

		foreach (var containingType in GetContainingTypes(typeSymbol))
		{
			builder.Append(containingType.Name);
			builder.Append('.');
		}

		builder.Append(typeSymbol.Name);
		builder.Append(".Fake.g.cs");
		return builder.ToString().Replace('<', '_').Replace('>', '_');
	}

	private static bool IsSupportedTargetType(TypeDeclarationSyntax declarationSyntax, INamedTypeSymbol typeSymbol, out string reason)
	{
		if (declarationSyntax is ClassDeclarationSyntax)
		{
			reason = string.Empty;
			return true;
		}

		if (declarationSyntax is RecordDeclarationSyntax recordDeclaration && !recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword))
		{
			reason = string.Empty;
			return true;
		}

		reason = "only classes and record classes can receive generated fake implementations";
		return false;
	}

	private static bool IsSupportedMethod(IMethodSymbol methodSymbol, out string reason)
	{
		if (UsesUnsupportedTypes(methodSymbol.ReturnType) || methodSymbol.Parameters.Any(static parameter => UsesUnsupportedTypes(parameter.Type)))
		{
			reason = "pointer and function pointer signatures are not supported";
			return false;
		}

		reason = string.Empty;
		return true;
	}

	private static bool IsSupportedProperty(IPropertySymbol propertySymbol, bool getterRequired, bool setterRequired, out string reason)
	{
		if (propertySymbol.ReturnsByRef || propertySymbol.ReturnsByRefReadonly)
		{
			reason = "ref-returning properties and indexers are not supported";
			return false;
		}

		if (UsesUnsupportedTypes(propertySymbol.Type) || propertySymbol.Parameters.Any(static parameter => UsesUnsupportedTypes(parameter.Type)))
		{
			reason = "pointer and function pointer signatures are not supported";
			return false;
		}

		reason = string.Empty;
		return true;
	}

	private static bool IsSupportedEvent(IEventSymbol eventSymbol, out string reason)
	{
		if (UsesUnsupportedTypes(eventSymbol.Type))
		{
			reason = "pointer and function pointer signatures are not supported";
			return false;
		}

		reason = string.Empty;
		return true;
	}

	private static bool UsesUnsupportedTypes(ITypeSymbol typeSymbol)
	{
		if (typeSymbol.TypeKind == TypeKind.Pointer || typeSymbol.TypeKind == TypeKind.FunctionPointer)
		{
			return true;
		}

		if (typeSymbol is IArrayTypeSymbol arrayType)
		{
			return UsesUnsupportedTypes(arrayType.ElementType);
		}

		if (typeSymbol is INamedTypeSymbol namedType)
		{
			foreach (var typeArgument in namedType.TypeArguments)
			{
				if (UsesUnsupportedTypes(typeArgument))
				{
					return true;
				}
			}
		}

		return false;
	}

	private static bool CanUseBuiltInHandler(ImmutableArray<IParameterSymbol> parameters, ITypeSymbol? returnType, bool returnsByRef, ITypeSymbol? trailingParameterType = null)
	{
		var parameterCount = parameters.Length + (trailingParameterType is null ? 0 : 1);
		if (returnsByRef || parameterCount > 16)
		{
			return false;
		}

		if (parameters.Any(static parameter => parameter.RefKind != RefKind.None || IsConcreteRefLikeType(parameter.Type)))
		{
			return false;
		}

		if (trailingParameterType is not null && IsConcreteRefLikeType(trailingParameterType))
		{
			return false;
		}

		return returnType is null || !IsConcreteRefLikeType(returnType);
	}

	private static bool CanGenerateMethodReturnHelper(IMethodSymbol methodSymbol)
	{
		if (methodSymbol.ReturnsVoid)
		{
			return false;
		}

		if (methodSymbol.ReturnsByRef || methodSymbol.ReturnsByRefReadonly)
		{
			return !IsConcreteRefLikeType(methodSymbol.ReturnType);
		}

		return CanCaptureConstantValue(methodSymbol.ReturnType);
	}

	private static bool CanGenerateParameterReturnHelper(IMethodSymbol methodSymbol)
	{
		return methodSymbol.Parameters.Length > 0
			&& methodSymbol.TypeParameters.Length == 0
			&& !methodSymbol.ReturnsVoid
			&& !methodSymbol.ReturnsByRef
			&& !methodSymbol.ReturnsByRefReadonly
			&& CanCaptureConstantValue(methodSymbol.ReturnType)
			&& methodSymbol.Parameters.All(static parameter => !parameter.IsParams && parameter.RefKind == RefKind.None && CanCaptureConstantValue(parameter.Type));
	}

	private static bool CanGeneratePropertyReturnHelper(IPropertySymbol propertySymbol, bool hasBackingField, bool getterRequired)
	{
		if (!getterRequired)
		{
			return false;
		}

		return hasBackingField || CanCaptureConstantValue(propertySymbol.Type);
	}

	private static bool CanGenerateWrappedResultHelper(ITypeSymbol returnType)
	{
		return TryGetWrappedResultType(returnType, out var wrappedResultType) && wrappedResultType is not null && CanCaptureConstantValue(wrappedResultType);
	}

	private static bool CanCaptureConstantValue(ITypeSymbol typeSymbol)
	{
		return !IsConcreteRefLikeType(typeSymbol) && !UsesUnsupportedTypes(typeSymbol);
	}

	private static bool CanStoreBackingValue(IPropertySymbol propertySymbol)
	{
		return !IsConcreteRefLikeType(propertySymbol.Type) && !UsesUnsupportedTypes(propertySymbol.Type);
	}

	private static bool TryGetWrappedResultType(ITypeSymbol returnType, out ITypeSymbol? wrappedResultType)
	{
		if (returnType is INamedTypeSymbol namedType && namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
		{
			if ((namedType.Name == "Task" || namedType.Name == "ValueTask") && namedType.TypeArguments.Length == 1)
			{
				wrappedResultType = namedType.TypeArguments[0];
				return true;
			}
		}

		wrappedResultType = null;
		return false;
	}

	private static string GetWrappedReturnExpression(ITypeSymbol returnType, string valueExpression)
	{
		if (returnType is INamedTypeSymbol namedType && namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
		{
			if (namedType.Name == "Task" && namedType.TypeArguments.Length == 1)
			{
				return $"global::System.Threading.Tasks.Task.FromResult<{GetTypeDisplay(namedType.TypeArguments[0])}>({valueExpression})";
			}

			if (namedType.Name == "ValueTask" && namedType.TypeArguments.Length == 1)
			{
				return $"new global::System.Threading.Tasks.ValueTask<{GetTypeDisplay(namedType.TypeArguments[0])}>({valueExpression})";
			}
		}

		return valueExpression;
	}

	private static string GetDefaultReturnExpression(ITypeSymbol returnType)
	{
		if (returnType is INamedTypeSymbol namedType && namedType.ContainingNamespace.ToDisplayString() == "System.Threading.Tasks")
		{
			if (namedType.Name == "Task" && namedType.TypeArguments.Length == 0)
			{
				return "global::System.Threading.Tasks.Task.CompletedTask";
			}

			if (namedType.Name == "Task" && namedType.TypeArguments.Length == 1)
			{
				return $"global::System.Threading.Tasks.Task.FromResult<{GetTypeDisplay(namedType.TypeArguments[0])}>(default!)";
			}

			if (namedType.Name == "ValueTask" && namedType.TypeArguments.Length == 0)
			{
				return "default(global::System.Threading.Tasks.ValueTask)";
			}

			if (namedType.Name == "ValueTask" && namedType.TypeArguments.Length == 1)
			{
				return $"new global::System.Threading.Tasks.ValueTask<{GetTypeDisplay(namedType.TypeArguments[0])}>(default({GetTypeDisplay(namedType.TypeArguments[0])}))";
			}
		}

		return "default!";
	}

	private static string GetBuiltInDelegateType(ImmutableArray<IParameterSymbol> parameters, ITypeSymbol? returnType, ITypeSymbol? trailingParameterType = null)
	{
		var genericArguments = parameters.Select(static parameter => GetTypeDisplay(parameter.Type)).ToList();
		if (trailingParameterType is not null)
		{
			genericArguments.Add(GetTypeDisplay(trailingParameterType));
		}

		if (returnType is null)
		{
			return genericArguments.Count == 0
				? "global::System.Action"
				: $"global::System.Action<{string.Join(", ", genericArguments)}>";
		}

		genericArguments.Add(GetTypeDisplay(returnType));
		return genericArguments.Count == 1
			? $"global::System.Func<{genericArguments[0]}>"
			: $"global::System.Func<{string.Join(", ", genericArguments)}>";
	}

	private static bool IsConcreteRefLikeType(ITypeSymbol typeSymbol)
	{
		return typeSymbol is INamedTypeSymbol { IsRefLikeType: true };
	}

	private static bool AreEquivalentMethodShapes(IMethodSymbol left, IMethodSymbol right)
	{
		return left.ReturnsByRef == right.ReturnsByRef
			&& left.ReturnsByRefReadonly == right.ReturnsByRefReadonly
			&& SymbolEqualityComparer.Default.Equals(left.ReturnType, right.ReturnType);
	}

	private static bool AreEquivalentPropertyShapes(IPropertySymbol left, IPropertySymbol right)
	{
		return SymbolEqualityComparer.Default.Equals(left.Type, right.Type)
			&& left.IsIndexer == right.IsIndexer;
	}

	private static bool HasMorePropertyAccessors(IPropertySymbol candidate, IPropertySymbol existing)
	{
		var candidateCount = (candidate.GetMethod is null ? 0 : 1) + (candidate.SetMethod is null ? 0 : 1);
		var existingCount = (existing.GetMethod is null ? 0 : 1) + (existing.SetMethod is null ? 0 : 1);
		return candidateCount > existingCount;
	}

	private static bool IsAbstractMemberImplemented(INamedTypeSymbol typeSymbol, ISymbol abstractMember)
	{
		for (var currentType = typeSymbol; currentType is not null; currentType = currentType.BaseType)
		{
			foreach (var candidate in currentType.GetMembers(abstractMember.Name))
			{
				switch (abstractMember)
				{
					case IMethodSymbol abstractMethod when candidate is IMethodSymbol candidateMethod:
						if (!candidateMethod.IsAbstract && SymbolEqualityComparer.Default.Equals(candidateMethod.OverriddenMethod, abstractMethod))
						{
							return true;
						}
						break;

					case IPropertySymbol abstractProperty when candidate is IPropertySymbol candidateProperty:
						if (SymbolEqualityComparer.Default.Equals(candidateProperty.OverriddenProperty, abstractProperty)
							&& ((candidateProperty.GetMethod is not { IsAbstract: true }) || (candidateProperty.SetMethod is not { IsAbstract: true })))
						{
							return true;
						}
						break;

					case IEventSymbol abstractEvent when candidate is IEventSymbol candidateEvent:
						if (!candidateEvent.IsAbstract && SymbolEqualityComparer.Default.Equals(candidateEvent.OverriddenEvent, abstractEvent))
						{
							return true;
						}
						break;
				}
			}
		}

		return false;
	}

	private static string GetMethodIdentityKey(IMethodSymbol methodSymbol)
	{
		var builder = new StringBuilder();
		builder.Append(methodSymbol.Name);
		builder.Append('`');
		builder.Append(methodSymbol.Arity);
		foreach (var parameter in methodSymbol.Parameters)
		{
			builder.Append('|');
			builder.Append(parameter.RefKind);
			builder.Append(':');
			builder.Append(GetTypeDisplay(parameter.Type));
		}

		return builder.ToString();
	}

	private static string GetPropertyIdentityKey(IPropertySymbol propertySymbol)
	{
		var builder = new StringBuilder();
		builder.Append(propertySymbol.Name);
		foreach (var parameter in propertySymbol.Parameters)
		{
			builder.Append('|');
			builder.Append(parameter.RefKind);
			builder.Append(':');
			builder.Append(GetTypeDisplay(parameter.Type));
		}

		return builder.ToString();
	}

	private static string BuildMethodBaseName(IMethodSymbol methodSymbol, bool needsOverloadSuffix)
	{
		if (!needsOverloadSuffix)
		{
			return methodSymbol.Name;
		}

		var parts = new List<string>();
		if (methodSymbol.Arity > 0)
		{
			parts.Add("Of" + methodSymbol.Arity);
		}

		foreach (var parameter in methodSymbol.Parameters)
		{
			var parameterToken = GetTypeToken(parameter.Type);
			if (parameter.RefKind != RefKind.None)
			{
				parameterToken = parameter.RefKind + parameterToken;
			}

			parts.Add(parameterToken);
		}

		return methodSymbol.Name + "_" + string.Join("_", parts);
	}

	private static string BuildPropertyBaseName(IPropertySymbol propertySymbol, bool needsOverloadSuffix)
	{
		var baseName = propertySymbol.IsIndexer ? "Item" : propertySymbol.Name;
		if (!needsOverloadSuffix)
		{
			return baseName;
		}

		return baseName + "_" + string.Join("_", propertySymbol.Parameters.Select(static parameter => GetTypeToken(parameter.Type)));
	}

	private static string GetTypeToken(ITypeSymbol typeSymbol)
	{
		return typeSymbol switch
		{
			IArrayTypeSymbol arrayType => GetTypeToken(arrayType.ElementType) + "Array",
			INamedTypeSymbol namedType when namedType.TypeArguments.Length > 0 =>
				SanitizeIdentifier(namedType.Name + "Of" + string.Concat(namedType.TypeArguments.Select(GetTypeToken))),
			_ => SanitizeIdentifier(typeSymbol.Name)
		};
	}

	private static string GetTypeDisplay(ITypeSymbol typeSymbol)
	{
		return typeSymbol.ToDisplayString(TypeNameFormat);
	}

	private static string GetTypeModifiers(TypeDeclarationSyntax syntax)
	{
		var modifiers = syntax.Modifiers
			.Where(static modifier => !modifier.IsKind(SyntaxKind.PartialKeyword))
			.Select(static modifier => modifier.Text)
			.ToArray();

		return modifiers.Length == 0 ? "partial " : string.Join(" ", modifiers) + " partial ";
	}

	private static string GetTypeKeyword(TypeDeclarationSyntax syntax)
	{
		return syntax switch
		{
			ClassDeclarationSyntax => "class",
			StructDeclarationSyntax => "struct",
			InterfaceDeclarationSyntax => "interface",
			RecordDeclarationSyntax recordDeclaration when recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.StructKeyword) => "record struct",
			RecordDeclarationSyntax recordDeclaration when recordDeclaration.ClassOrStructKeyword.IsKind(SyntaxKind.ClassKeyword) => "record class",
			RecordDeclarationSyntax => "record",
			_ => "class"
		};
	}

	private static string GetMemberAccessibility(Accessibility accessibility)
	{
		return accessibility switch
		{
			Accessibility.Public => "public",
			Accessibility.Private => "private",
			Accessibility.Internal => "internal",
			Accessibility.Protected => "protected",
			Accessibility.ProtectedAndInternal => "private protected",
			Accessibility.ProtectedOrInternal => "protected internal",
			_ => "private"
		};
	}

	private static string GetAccessorAccessibility(IMethodSymbol accessor, Accessibility propertyAccessibility)
	{
		return accessor.DeclaredAccessibility == propertyAccessibility ? string.Empty : GetMemberAccessibility(accessor.DeclaredAccessibility);
	}

	private static string GetParameterModifier(RefKind refKind)
	{
		return refKind switch
		{
			RefKind.Ref => "ref",
			RefKind.Out => "out",
			RefKind.In => "in",
			RefKind.RefReadOnlyParameter => "ref readonly",
			_ => string.Empty
		};
	}

	private static string GetArgumentModifier(RefKind refKind)
	{
		return refKind switch
		{
			RefKind.Ref => "ref",
			RefKind.Out => "out",
			RefKind.In => "in",
			RefKind.RefReadOnlyParameter => "in",
			_ => string.Empty
		};
	}

	private static IEnumerable<INamedTypeSymbol> GetContainingTypes(INamedTypeSymbol typeSymbol)
	{
		var types = new Stack<INamedTypeSymbol>();
		for (var containingType = typeSymbol.ContainingType; containingType is not null; containingType = containingType.ContainingType)
		{
			types.Push(containingType);
		}

		return types;
	}

	private static SyntaxNode? TryGetDeclarationSyntax(INamedTypeSymbol symbol)
	{
		return symbol.DeclaringSyntaxReferences.FirstOrDefault()?.GetSyntax();
	}

	private static bool HasPartialModifier(TypeDeclarationSyntax syntax)
	{
		return syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.PartialKeyword));
	}

	private static bool HasFileModifier(TypeDeclarationSyntax syntax)
	{
		return syntax.Modifiers.Any(static modifier => modifier.IsKind(SyntaxKind.FileKeyword));
	}

	private static string EscapeIdentifier(string identifier)
	{
		if (SyntaxFacts.GetKeywordKind(identifier) != SyntaxKind.None || SyntaxFacts.GetContextualKeywordKind(identifier) != SyntaxKind.None)
		{
			return "@" + identifier;
		}

		return identifier;
	}

	private static string SanitizeIdentifier(string identifier)
	{
		if (string.IsNullOrWhiteSpace(identifier))
		{
			return "Generated";
		}

		var builder = new StringBuilder(identifier.Length + 1);
		if (!SyntaxFacts.IsIdentifierStartCharacter(identifier[0]))
		{
			builder.Append('_');
		}

		foreach (var character in identifier)
		{
			builder.Append(SyntaxFacts.IsIdentifierPartCharacter(character) ? character : '_');
		}

		return builder.ToString();
	}

	private static string ToCamelCase(string identifier)
	{
		if (string.IsNullOrEmpty(identifier))
		{
			return identifier;
		}

		return char.ToLowerInvariant(identifier[0]) + identifier.Substring(1);
	}

	private static void AppendIndent(SourceWriter builder, int indentation)
	{
		builder.Indent = indentation;
	}

	private sealed class MemberCollection
	{
		public List<MethodRequirement> Methods { get; } = new();

		public List<PropertyRequirement> Properties { get; } = new();

		public List<EventRequirement> Events { get; } = new();

		public List<Diagnostic> Diagnostics { get; } = new();
	}

	private sealed class MethodRequirement
	{
		public MethodRequirement(IMethodSymbol symbol, bool requiresOverride, bool requiresPublic)
		{
			Symbol = symbol;
			RequiresOverride = requiresOverride;
			RequiresPublic = requiresPublic;
		}

		public IMethodSymbol Symbol { get; set; }

		public bool RequiresOverride { get; set; }

		public bool RequiresPublic { get; set; }
	}

	private sealed record MethodPublicNames(
		string OnMethodName,
		string CallCountName,
		string? ReturnsName,
		string? ReturnsResultName);

	private sealed class PropertyRequirement
	{
		public PropertyRequirement(IPropertySymbol symbol, bool requiresOverride, bool requiresPublic, IMethodSymbol? getter, IMethodSymbol? setter)
		{
			Symbol = symbol;
			RequiresOverride = requiresOverride;
			RequiresPublic = requiresPublic;
			Getter = getter;
			Setter = setter;
		}

		public IPropertySymbol Symbol { get; set; }

		public bool RequiresOverride { get; set; }

		public bool RequiresPublic { get; set; }

		public IMethodSymbol? Getter { get; set; }

		public IMethodSymbol? Setter { get; set; }
	}

	private sealed class EventRequirement
	{
		public EventRequirement(IEventSymbol symbol, bool requiresOverride, bool requiresPublic)
		{
			Symbol = symbol;
			RequiresOverride = requiresOverride;
			RequiresPublic = requiresPublic;
		}

		public IEventSymbol Symbol { get; set; }

		public bool RequiresOverride { get; set; }

		public bool RequiresPublic { get; set; }
	}

	private sealed record MethodPlan(
		IMethodSymbol Symbol,
		bool RequiresOverride,
		string CustomDelegateName,
		bool ExposeCustomDelegate,
		string OnMethodName,
		string OnMethodParameterType,
		string? HandlerFieldName,
		string? HandlerMapName,
		string? HandlerKeyMethodName,
		string CallCountName,
		string? ReturnsName,
		string? ReturnsResultName,
		string? ParameterReturnKeyName,
		string? ParameterReturnMapName,
		string? ResultFieldName,
		string? ResultMapName,
		string? ResultHolderName,
		string? ResultHolderMethodName,
		bool EmitCallCount,
		bool EmitReturnsDefaultHelper,
		bool EmitReturnsResultDefaultHelper,
		bool UsesBuiltInHandler);

	private sealed record PropertyPlan(
		IPropertySymbol Symbol,
		bool RequiresOverride,
		IMethodSymbol? Getter,
		IMethodSymbol? Setter,
		string? GetterDelegateName,
		string? SetterDelegateName,
		bool ExposeCustomGetterDelegate,
		bool ExposeCustomSetterDelegate,
		string? GetHandlerTypeName,
		string? SetHandlerTypeName,
		string? GetHandlerFieldName,
		string? SetHandlerFieldName,
		string? BackingFieldName,
		string? GetCallCountName,
		string? SetCallCountName,
		string? OnGetName,
		string? OnSetName,
		string? ReturnsName,
		string? ReturnsResultName,
		bool GetterUsesBuiltInHandler,
		bool SetterUsesBuiltInHandler);

	private sealed record EventPlan(
		IEventSymbol Symbol,
		bool RequiresOverride,
		string BackingFieldName,
		string AddCallCountName,
		string RemoveCallCountName,
		string RaiseMethodName);

	private sealed class SourceWriter : IDisposable
	{
		private readonly StringWriter _textWriter = new();
		private readonly IndentedTextWriter _writer;

		public SourceWriter()
		{
			_writer = new IndentedTextWriter(_textWriter, "    ");
		}

		public int Indent
		{
			get => _writer.Indent;
			set => _writer.Indent = value;
		}

		public void Append(string? value)
		{
			_writer.Write(value);
		}

		public void Append(char value)
		{
			_writer.Write(value);
		}

		public void AppendLine()
		{
			_writer.WriteLine();
		}

		public void AppendLine(string? value)
		{
			_writer.WriteLine(value);
		}

		public override string ToString()
		{
			return _textWriter.ToString();
		}

		public void Dispose()
		{
			_writer.Dispose();
			_textWriter.Dispose();
		}
	}

	private sealed class NameAllocator
	{
		private readonly HashSet<string> _names;

		public NameAllocator(IEnumerable<string> existingNames)
		{
			_names = new HashSet<string>(existingNames, StringComparer.Ordinal);
		}

		public string Reserve(string preferredName)
		{
			var sanitizedName = SanitizeIdentifier(preferredName);
			if (_names.Add(sanitizedName))
			{
				return sanitizedName;
			}

			for (var suffix = 1; ; suffix++)
			{
				var candidate = sanitizedName + suffix;
				if (_names.Add(candidate))
				{
					return candidate;
				}
			}
		}
	}

	private enum ParameterRenderingMode
	{
		MemberDeclaration,
		DelegateDeclaration,
		AnonymousMethod,
		ArgumentList,
	}
}