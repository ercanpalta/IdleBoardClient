public static class PowerManagement
{
    /// <summary>
    /// Ekranın kararmasını ve sistemin uykuya geçmesini engeller.
    /// </summary>
    public static void UykuModunuEngelle()
    {
        NativeMethods.SetThreadExecutionState(
            NativeMethods.EXECUTION_STATE.ES_CONTINUOUS |
            NativeMethods.EXECUTION_STATE.ES_DISPLAY_REQUIRED |
            NativeMethods.EXECUTION_STATE.ES_SYSTEM_REQUIRED
        );
    }

    /// <summary>
    /// Windows varsayılan güç politikasını geri yükler.
    /// </summary>
    public static void UykuModunuSerbestBirak()
    {
        NativeMethods.SetThreadExecutionState(NativeMethods.EXECUTION_STATE.ES_CONTINUOUS);
    }
}