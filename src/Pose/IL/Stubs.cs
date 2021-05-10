using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using System.Runtime.CompilerServices;
using System.Runtime.Serialization;
using Mono.Reflection;
using Pose.Extensions;
using Pose.Helpers;
using Pose.IL.DebugHelpers;

namespace Pose.IL
{
    internal static class Stubs
    {
        private static MethodInfo s_getMethodFromHandleMethod;

        private static MethodInfo s_createRewriterMethod;

        private static MethodInfo s_rewriteMethod;

        private static MethodInfo s_getMethodPointerMethod;

        private static MethodInfo s_devirtualizeMethodMethod;

        static Stubs()
        {
            s_getMethodFromHandleMethod = typeof(MethodBase).GetMethod("GetMethodFromHandle", new Type[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) });
            s_createRewriterMethod = typeof(MethodRewriter).GetMethod("CreateRewriter", new Type[] { typeof(MethodBase), typeof(bool) });
            s_rewriteMethod = typeof(MethodRewriter).GetMethod("Rewrite");
            s_getMethodPointerMethod = typeof(StubHelper).GetMethod("GetMethodPointer");
            s_devirtualizeMethodMethod = typeof(StubHelper).GetMethod("DevirtualizeMethod", new Type[] { typeof(object), typeof(MethodInfo) });
        }

        public static DynamicMethod GenerateStubForDirectCall(MethodBase method)
        {
            Type returnType = method.IsConstructor ? typeof(void) : (method as MethodInfo).ReturnType;
            List<Type> signatureParamTypes = new List<Type>();
            if (!method.IsStatic)
            {
                Type thisType = method.DeclaringType;
                if (thisType.IsValueType)
                {
                    thisType = thisType.MakeByRefType();
                }

                signatureParamTypes.Add(thisType);
            }

            signatureParamTypes.AddRange(method.GetParameters().Select(p => p.ParameterType));

            DynamicMethod stub = new DynamicMethod(
                StubHelper.CreateStubNameFromMethod("stub", method),
                returnType,
                signatureParamTypes.ToArray(),
                StubHelper.GetOwningModule(),
                true);

            ILGenerator ilGenerator = stub.GetILGenerator();

            if (method.GetMethodBody() == null || StubHelper.IsIntrinsic(method))
            {
                // Method has no body or is a compiler intrinsic,
                // simply forward arguments to original or shim
                for (int i = 0; i < signatureParamTypes.Count; i++)
                {
                    ilGenerator.Emit(OpCodes.Ldarg, i);
                }

                if (method.IsConstructor)
                {
                    ilGenerator.Emit(OpCodes.Call, (ConstructorInfo)method);
                }
                else
                {
                    ilGenerator.Emit(OpCodes.Call, (MethodInfo)method);
                }

                ilGenerator.Emit(OpCodes.Ret);
                return stub;
            }

            ilGenerator.DeclareLocal(typeof(IntPtr));

            Label rewriteLabel = ilGenerator.DefineLabel();
            Label returnLabel = ilGenerator.DefineLabel();

            // Inject method info into instruction stream
            if (method.IsConstructor)
            {
                ilGenerator.Emit(OpCodes.Ldtoken, (ConstructorInfo)method);
            }
            else
            {
                ilGenerator.Emit(OpCodes.Ldtoken, (MethodInfo)method);
            }
            ilGenerator.Emit(OpCodes.Ldtoken, method.DeclaringType);
            ilGenerator.Emit(OpCodes.Call, s_getMethodFromHandleMethod);

            // Rewrite method
            ilGenerator.MarkLabel(rewriteLabel);
            ilGenerator.Emit(OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Call, s_createRewriterMethod);
            ilGenerator.Emit(OpCodes.Call, s_rewriteMethod);

            // Retrieve pointer to rewritten method
            ilGenerator.Emit(OpCodes.Call, s_getMethodPointerMethod);
            ilGenerator.Emit(OpCodes.Stloc_0);

            // Setup stack and make indirect call
            for (int i = 0; i < signatureParamTypes.Count; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i);
            }
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, returnType, signatureParamTypes.ToArray(), null);

            ilGenerator.MarkLabel(returnLabel);
            ilGenerator.Emit(OpCodes.Ret);

            return stub;
        }

        public static DynamicMethod GenerateStubForVirtualCall(MethodInfo method, TypeInfo constrainedType)
        {
            Type thisType = constrainedType.MakeByRefType();
            MethodInfo actualMethod = StubHelper.DevirtualizeMethod(constrainedType, method);

            List<Type> signatureParamTypes = new List<Type>();
            signatureParamTypes.Add(thisType);
            signatureParamTypes.AddRange(method.GetParameters().Select(p => p.ParameterType));

            DynamicMethod stub = new DynamicMethod(
                StubHelper.CreateStubNameFromMethod("stub_virt", method),
                method.ReturnType,
                signatureParamTypes.ToArray(),
                StubHelper.GetOwningModule(),
                true);
            
            ILGenerator ilGenerator = stub.GetILGenerator();

            ilGenerator.DeclareLocal(typeof(IntPtr));

            Label rewriteLabel = ilGenerator.DefineLabel();
            Label returnLabel = ilGenerator.DefineLabel();

            // Inject method info into instruction stream
            ilGenerator.Emit(OpCodes.Ldtoken, actualMethod);
            ilGenerator.Emit(OpCodes.Ldtoken, actualMethod.DeclaringType);
            ilGenerator.Emit(OpCodes.Call, s_getMethodFromHandleMethod);
            ilGenerator.Emit(OpCodes.Castclass, typeof(MethodInfo));

            // Rewrite method
            ilGenerator.MarkLabel(rewriteLabel);
            ilGenerator.Emit(OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Call, s_createRewriterMethod);
            ilGenerator.Emit(OpCodes.Call, s_rewriteMethod);
            ilGenerator.Emit(OpCodes.Castclass, typeof(MethodInfo));

            // Retrieve pointer to rewritten method
            ilGenerator.Emit(OpCodes.Call, s_getMethodPointerMethod);
            ilGenerator.Emit(OpCodes.Stloc_0);

            // Setup stack and make indirect call
            for (int i = 0; i < signatureParamTypes.Count; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i);
                if (i == 0)
                {
                    if (!constrainedType.IsValueType)
                    {
                        ilGenerator.Emit(OpCodes.Ldind_Ref);
                        signatureParamTypes[i] = constrainedType;
                    }
                    else
                    {
                        if (actualMethod.DeclaringType != constrainedType)
                        {
                            ilGenerator.Emit(OpCodes.Ldobj, constrainedType);
                            ilGenerator.Emit(OpCodes.Box, constrainedType);
                            signatureParamTypes[i] = actualMethod.DeclaringType;
                        }
                    }
                }
            }
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, method.ReturnType, signatureParamTypes.ToArray(), null);

            ilGenerator.MarkLabel(returnLabel);
            ilGenerator.Emit(OpCodes.Ret);

            return stub;
        }

        public static DynamicMethod GenerateStubForVirtualCall(MethodInfo method)
        {
            Type thisType = method.DeclaringType.IsInterface ? typeof(object) : method.DeclaringType;

            List<Type> signatureParamTypes = new List<Type>();
            signatureParamTypes.Add(thisType);
            signatureParamTypes.AddRange(method.GetParameters().Select(p => p.ParameterType));

            DynamicMethod stub = new DynamicMethod(
                StubHelper.CreateStubNameFromMethod("stub_virt", method),
                method.ReturnType,
                signatureParamTypes.ToArray(),
                StubHelper.GetOwningModule(),
                true);

            ILGenerator ilGenerator = stub.GetILGenerator();

            if ((method.GetMethodBody() == null && !method.IsAbstract) || StubHelper.IsIntrinsic(method))
            {
                // Method has no body or is a compiler intrinsic,
                // simply forward arguments to original or shim
                for (int i = 0; i < signatureParamTypes.Count; i++)
                {
                    ilGenerator.Emit(OpCodes.Ldarg, i);
                }

                ilGenerator.Emit(OpCodes.Callvirt, method);
                ilGenerator.Emit(OpCodes.Ret);
                return stub;
            }

            ilGenerator.DeclareLocal(typeof(MethodInfo));
            ilGenerator.DeclareLocal(typeof(IntPtr));

            Label rewriteLabel = ilGenerator.DefineLabel();
            Label returnLabel = ilGenerator.DefineLabel();

            // Inject method info into instruction stream
            ilGenerator.Emit(OpCodes.Ldtoken, method);
            ilGenerator.Emit(OpCodes.Ldtoken, method.DeclaringType);
            ilGenerator.Emit(OpCodes.Call, s_getMethodFromHandleMethod);
            ilGenerator.Emit(OpCodes.Castclass, typeof(MethodInfo));
            ilGenerator.Emit(OpCodes.Stloc_0);

            // Resolve virtual method to object type
            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Call, s_devirtualizeMethodMethod);

            // Rewrite resolved method
            ilGenerator.MarkLabel(rewriteLabel);
            ilGenerator.Emit(method.DeclaringType.IsInterface ? OpCodes.Ldc_I4_1 : OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Call, s_createRewriterMethod);
            ilGenerator.Emit(OpCodes.Call, s_rewriteMethod);
            ilGenerator.Emit(OpCodes.Castclass, typeof(MethodInfo));

            // Retrieve pointer to rewritten method
            ilGenerator.Emit(OpCodes.Call, s_getMethodPointerMethod);
            ilGenerator.Emit(OpCodes.Stloc_1);

            // Setup stack and make indirect call
            for (int i = 0; i < signatureParamTypes.Count; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i);
            }
            ilGenerator.Emit(OpCodes.Ldloc_1);
            ilGenerator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, method.ReturnType, signatureParamTypes.ToArray(), null);

            ilGenerator.MarkLabel(returnLabel);
            ilGenerator.Emit(OpCodes.Ret);

            return stub;
        }

        public static DynamicMethod GenerateStubForObjectInitialization(ConstructorInfo constructor)
        {
            Type thisType = constructor.DeclaringType;
            if (thisType.IsValueType)
            {
                thisType = thisType.MakeByRefType();
            }

            List<Type> signatureParamTypes = new List<Type>();
            signatureParamTypes.Add(thisType);
            signatureParamTypes.AddRange(constructor.GetParameters().Select(p => p.ParameterType));

            DynamicMethod stub = new DynamicMethod(
                StubHelper.CreateStubNameFromMethod("stub_ctor", constructor),
                constructor.DeclaringType,
                signatureParamTypes.Skip(1).ToArray(),
                StubHelper.GetOwningModule(),
                true);
            
            ILGenerator ilGenerator = stub.GetILGenerator();

            if (constructor.GetMethodBody() == null || StubHelper.IsIntrinsic(constructor))
            {
                // Constructor has no body or is a compiler intrinsic,
                // simply forward arguments to original or shim
                for (int i = 0; i < signatureParamTypes.Count - 1; i++)
                {
                    ilGenerator.Emit(OpCodes.Ldarg, i);
                }

                ilGenerator.Emit(OpCodes.Newobj, constructor);
                ilGenerator.Emit(OpCodes.Ret);
                return stub;
            }

            ilGenerator.DeclareLocal(typeof(IntPtr));
            ilGenerator.DeclareLocal(constructor.DeclaringType);

            Label rewriteLabel = ilGenerator.DefineLabel();
            Label returnLabel = ilGenerator.DefineLabel();

            // Inject method info into instruction stream
            ilGenerator.Emit(OpCodes.Ldtoken, constructor);
            ilGenerator.Emit(OpCodes.Ldtoken, constructor.DeclaringType);
            ilGenerator.Emit(OpCodes.Call, s_getMethodFromHandleMethod);

            // Rewrite method
            ilGenerator.MarkLabel(rewriteLabel);
            ilGenerator.Emit(OpCodes.Ldc_I4_0);
            ilGenerator.Emit(OpCodes.Call, s_createRewriterMethod);
            ilGenerator.Emit(OpCodes.Call, s_rewriteMethod);

            // Retrieve pointer to rewritten method
            ilGenerator.Emit(OpCodes.Call, s_getMethodPointerMethod);
            ilGenerator.Emit(OpCodes.Stloc_0);

            if (constructor.DeclaringType.IsValueType)
            {
                ilGenerator.Emit(OpCodes.Ldloca_S, (byte)1);
                ilGenerator.Emit(OpCodes.Dup);
                ilGenerator.Emit(OpCodes.Initobj, constructor.DeclaringType);
            }
            else
            {
                ilGenerator.Emit(OpCodes.Ldtoken, constructor.DeclaringType);
                ilGenerator.Emit(OpCodes.Call, typeof(Type).GetMethod("GetTypeFromHandle"));
                ilGenerator.Emit(OpCodes.Call, typeof(RuntimeHelpers).GetMethod("GetUninitializedObject"));
                ilGenerator.Emit(OpCodes.Dup);
                ilGenerator.Emit(OpCodes.Stloc_1);
            }

            // Setup stack and make indirect call
            for (int i = 0; i < signatureParamTypes.Count - 1; i++)
            {
                ilGenerator.Emit(OpCodes.Ldarg, i);
            }
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.EmitCalli(OpCodes.Calli, CallingConventions.Standard, typeof(void), signatureParamTypes.ToArray(), null);

            ilGenerator.MarkLabel(returnLabel);
            ilGenerator.Emit(OpCodes.Ldloc_1);
            ilGenerator.Emit(OpCodes.Ret);

            return stub;
        }

        public static DynamicMethod GenerateStubForMethodPointer(MethodInfo methodInfo)
        {
            List<Type> parameterTypes = new List<Type>();
            parameterTypes.Add(typeof(RuntimeMethodHandle));
            parameterTypes.Add(typeof(RuntimeTypeHandle));

            DynamicMethod stub = new DynamicMethod(
                string.Format("stub_ftn_{0}_{1}", methodInfo.DeclaringType, methodInfo.Name),
                typeof(IntPtr),
                parameterTypes.ToArray(),
                StubHelper.GetOwningModule(),
                true);

            ILGenerator ilGenerator = stub.GetILGenerator();
            ilGenerator.DeclareLocal(typeof(MethodInfo));

            ilGenerator.Emit(OpCodes.Ldarg_0);
            ilGenerator.Emit(OpCodes.Ldarg_1);
            ilGenerator.Emit(OpCodes.Call, typeof(MethodBase).GetMethod("GetMethodFromHandle", new Type[] { typeof(RuntimeMethodHandle), typeof(RuntimeTypeHandle) }));
            ilGenerator.Emit(OpCodes.Castclass, typeof(MethodInfo));
            ilGenerator.Emit(OpCodes.Stloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Call, typeof(MethodRewriter).GetMethod("CreateRewriter", new Type[] { typeof(MethodBase) }));
            ilGenerator.Emit(OpCodes.Call, typeof(MethodRewriter).GetMethod("Rewrite"));
            ilGenerator.Emit(OpCodes.Stloc_0);
            ilGenerator.Emit(OpCodes.Ldloc_0);
            ilGenerator.Emit(OpCodes.Call, typeof(StubHelper).GetMethod("GetMethodPointer"));
            ilGenerator.Emit(OpCodes.Ret);
            return stub;
        }
    }
}