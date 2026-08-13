using System.Runtime.CompilerServices;

namespace PaperTodo;

internal static class NativeBatchReentrancyRegression
{
    [ModuleInitializer]
    internal static void Run()
    {
        if (!EdgeCapsuleNativeTransactionPolicy.ShouldDeferSharedFrameForNativeApply(
                nativeBatchApplyActive: true))
        {
            throw new InvalidOperationException(
                "A shared render frame must defer while a controller-owned native apply is active.");
        }

        if (EdgeCapsuleNativeTransactionPolicy.ShouldDeferSharedFrameForNativeApply(
                nativeBatchApplyActive: false))
        {
            throw new InvalidOperationException(
                "An idle native apply state must not block the shared render frame.");
        }
    }
}
