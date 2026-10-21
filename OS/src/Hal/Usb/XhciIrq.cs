namespace OS.Hal.Usb
{
    // Interrupter 0 on MSI (plan §11, stages 1-2).
    //
    // Until EnableInterrupts the controller is polled and every user holds the
    // machine for a whole transfer (Preemption.Suppress), as before. After it
    // TryWaitEvent sleeps while the event ring is empty and is woken by the
    // interrupt (IrqWait), and users exclude each other with a lock instead:
    // the rings carry one enqueue index apiece and nothing tells two
    // completions apart, so one transfer owns the controller from doorbell to
    // event either way.
    internal sealed unsafe partial class XhciController
    {
        private const uint USBCMD_INTE = 1u << 2;
        private const uint USBSTS_EINT = 1u << 3;
        private const uint IMAN_IP = 1u << 0;
        private const uint IMAN_IE = 1u << 1;
        private const ulong ERDP_EHB = 1UL << 3;

        // 160 × 250 ns = 40 µs between interrupts at most: a burst of events
        // costs one interrupt, a lone one waits no longer than that.
        private const uint ImodInterval = 160;

        private int _irqSource = -1;
        private OS.Kernel.Threading.OwnedLock _lock;

        private static XhciController[] s_bySource;

        /// <summary>The interrupt source, -1 while the controller is polled.</summary>
        public int InterruptSource => _irqSource;

        /// <summary>
        /// Switches the controller to MSI once the local APIC is ours. False
        /// (and polling) without a vector or MSI.
        /// </summary>
        public bool EnableInterrupts()
        {
            if (_irqSource >= 0) return true;
            if (!_running || !OS.Hal.Apic.LocalApic.IsEnabled) return false;

            int source = DeviceIrq.Register(&OnInterrupt);
            if (source < 0)
            {
                Log.Write(LogLevel.Warn, "[irq] xhci: no vector - polling");
                return false;
            }

            if (!TryFindPci(out Pci.PciDev dev)
                || !Msi.TryEnable(dev, DeviceIrq.VectorOf(source), out string kind))
            {
                Log.Write(LogLevel.Warn, "[irq] xhci: no MSI - polling");
                return false;
            }

            s_bySource ??= new XhciController[OS.Kernel.Threading.IrqWait.MaxSources];
            s_bySource[source] = this;
            _lock = new OS.Kernel.Threading.OwnedLock();

            ulong ir = _runtimeBase + IR0;
            Write32(ir + IR_IMOD, ImodInterval);
            Write32(ir + IR_IMAN, IMAN_IE | IMAN_IP);
            Write32(_opBase + OP_USBSTS, USBSTS_EINT);
            Write32(_opBase + OP_USBCMD, Read32(_opBase + OP_USBCMD) | USBCMD_INTE);
            _irqSource = source;

            Log.Begin(LogLevel.Info);
            Console.Write("[irq] xhci ");
            Console.Write(kind);
            Console.Write(" vector=0x");
            Console.WriteHex(DeviceIrq.VectorOf(source));
            Console.Write(" pci=");
            Console.WriteInt(_pci.Bus);
            Console.Write(":");
            Console.WriteInt(_pci.Slot);
            Console.Write(".");
            Console.WriteInt(_pci.Func);
            Log.EndLine();
            return true;
        }

        private bool TryFindPci(out Pci.PciDev dev)
        {
            for (int i = 0; i < Pci.Count; i++)
            {
                dev = Pci.Get(i);
                if (dev.Bus == _pci.Bus && dev.Slot == _pci.Slot && dev.Func == _pci.Func) return true;
            }
            dev = default;
            return false;
        }

        // Interrupts off: acknowledge the interrupter (IP and EINT are
        // write-1-to-clear) and wake whoever waits on the ring. The events
        // themselves stay where they are for TryWaitEvent.
        //
        // Not filtered on IMAN.IP: with MSI or MSI-X the controller clears IP
        // itself once the message is sent (xHCI 5.5.2.1), so by the time we
        // look it reads 0 — the first version woke nobody and every wait ran
        // to its one-second deadline. The handler does not know which vector
        // brought it, so every controller on interrupts is posted; a waiter
        // woken for nothing finds its ring empty and sleeps again.
        private static void OnInterrupt()
        {
            for (int source = 0; s_bySource != null && source < s_bySource.Length; source++)
            {
                XhciController hc = s_bySource[source];
                if (hc == null) continue;
                *(uint*)(hc._runtimeBase + IR0 + IR_IMAN) = IMAN_IE | IMAN_IP;
                *(uint*)(hc._opBase + OP_USBSTS) = USBSTS_EINT;
                OS.Kernel.Threading.IrqWait.Post(source);
            }
        }

        // TryWaitEvent's condition for IrqWait.Sleep: an event at the dequeue
        // index whose cycle bit is ours.
        private static bool EventReady(int source)
        {
            XhciController hc = s_bySource[source];
            ulong slot = hc._eventRing + hc._eventDequeue * (ulong)TrbSize;
            return (hc.Read32(slot + 12) & TRB_CYCLE) == hc._eventCycle;
        }

        /// <summary>
        /// Takes the controller for one transfer: the lock with interrupts,
        /// the CPU without. Hand the result to <see cref="Leave"/>.
        /// </summary>
        public static bool Enter(XhciController hc)
        {
            if (hc != null && hc._irqSource >= 0)
            {
                hc._lock.Enter();
                return true;
            }
            OS.Kernel.Threading.Preemption.Suppress();
            return false;
        }

        public static void Leave(XhciController hc, bool locked)
        {
            if (locked) hc._lock.Leave();
            else OS.Kernel.Threading.Preemption.Allow();
        }

        /// <summary>Every running controller to MSI (ExitBootServicesProbe, once the APIC is ours).</summary>
        public static void EnableInterruptsAll()
        {
            for (int i = 0; i < Xhci.Count; i++) Xhci.Get(i).EnableInterrupts();
        }
    }
}
