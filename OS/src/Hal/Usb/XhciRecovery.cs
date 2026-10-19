namespace OS.Hal.Usb
{
    // Bringing the default control endpoint back after a device refuses
    // something.
    //
    // A STALL is a normal answer in USB, not a fault: a device says "not this
    // one" to a request it does not implement, and the host is expected to
    // clear the condition and carry on. On xHCI it is not a per-transfer
    // result — the endpoint itself goes to Halted and executes nothing further.
    // Until this file, nothing cleared it, so the first refusal of a device's
    // life silently ended every control transfer to it.
    //
    // What that cost, measured on the rig 2026-09-25: after configuration the
    // census could not read a single descriptor, every request timing out with
    // no completion event at all. The reading that settled it was
    // `epState=2(HALTED)` while the slot sat correctly at 3 (Configured) — the
    // command had worked, the endpoint had not. The refusal itself came from a
    // class request whose result the caller discarded, so nothing had reported
    // it either.
    //
    // Recovery is two commands, in this order (xHCI 4.6.8, 4.6.10):
    //   Reset Endpoint        Halted -> Stopped
    //   Set TR Dequeue Pointer  tell it where to resume, with our cycle bit
    // Without the second, the controller resumes at the TRB that stalled and
    // stalls again.
    internal sealed unsafe partial class XhciController
    {
        private const uint TRB_RESET_ENDPOINT = 14;
        private const uint TRB_STOP_ENDPOINT = 15;
        private const uint TRB_SET_TR_DEQUEUE = 16;

        // Endpoint ID of the default control endpoint, in the field layout the
        // command TRBs use (bits 20:16 of dword 3).
        private const uint EPID_CONTROL = 1u << 16;

        private uint _ep0Halts;
        private uint _ep0Recovered;

        /// <summary>How many times the control endpoint was found halted.</summary>
        public uint ControlHaltsSeen => _ep0Halts;

        /// <summary>How many of those were brought back.</summary>
        public uint ControlHaltsRecovered => _ep0Recovered;

        /// <summary>
        /// Called after a control transfer fails. Does nothing unless the
        /// endpoint is actually halted, so the ordinary "device said no and
        /// carried on" costs two register reads.
        /// </summary>
        private void RecoverControlEndpoint(uint slotId)
        {
            if (!TryReadControlEndpointState(slotId, out _, out uint epState,
                                             out _, out _, out _, out _, out _))
                return;

            // 2 halted, 4 error. Anything else is a failure the endpoint
            // survived, and resetting it would throw away work in flight.
            if (epState != 2 && epState != 4) return;

            _ep0Halts++;
            if (TryResetControlEndpoint(slotId)) _ep0Recovered++;
        }

        /// <summary>
        /// Take back a transfer the caller queued and then gave up on, and
        /// leave the endpoint ready for the next one.
        /// </summary>
        /// <remarks>
        /// A read that times out is not finished — the TRB is still on the
        /// ring, and the device may fill it a second later. That matters more
        /// here than it looks: TryWaitEvent matches on event type and slot,
        /// and on the test rig the stick, the keyboard and the serial port are
        /// one device on one slot. An abandoned serial read would therefore be
        /// collected by whatever waits next — a disk read, say, which would
        /// return believing it had its sector.
        ///
        /// So nothing is left queued across calls: Stop Endpoint takes the
        /// transfer back, Set TR Dequeue points at where the next one will be
        /// written, and the Stopped event the controller posts for the aborted
        /// TD is drained here rather than left for a stranger.
        /// </remarks>
        // Nothing calls this today, and that is the fix rather than an
        // oversight: the serial read was cancelling its own transfer on every
        // empty poll, which is where the machine's idle time was going. Kept
        // because taking a transfer back is a real operation with no other
        // implementation - an unplug, or a caller that genuinely has to reclaim
        // its buffer, needs exactly this - and because writing it again from the
        // specification would be worse than reading it here.
        private void AbortEndpointTransfer(uint slotId, uint dci, ulong ring, uint enqueue, uint cycle)
        {
            ulong started = OS.Kernel.Diagnostics.PerfCounters.Now();
            uint epid = dci << 16;

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = 0; trb[1] = 0; trb[2] = 0;
            trb[3] = (TRB_STOP_ENDPOINT << 10) | epid | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            TryWaitEvent(TRB_CMD_COMPLETE, 1000, out _, out _);

            // The aborted TD posts a transfer event of its own; take it now,
            // without blocking, so it cannot complete somebody else later.
            TryWaitEvent(TRB_TRANSFER_EVENT, slotId, 0, out _, out _);

            ulong resume = ring + enqueue * (ulong)TrbSize;
            trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)(resume | (cycle & 1));
            trb[1] = (uint)(resume >> 32);
            trb[2] = 0;
            trb[3] = (TRB_SET_TR_DEQUEUE << 10) | epid | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            TryWaitEvent(TRB_CMD_COMPLETE, 1000, out _, out _);

            OS.Kernel.Diagnostics.PerfCounters.CountTimed(
                OS.Kernel.Diagnostics.PerfCounter.UsbAborts,
                OS.Kernel.Diagnostics.PerfCounter.UsbAbortTicks, started);
        }

        /// <summary>
        /// The host side of a stick's bulk endpoint back to a clean, empty
        /// state (step208), after a STALL or a transfer that never completed.
        /// </summary>
        /// <remarks>
        /// Halted: Reset Endpoint takes it to Stopped. Still running, which is
        /// what a timeout leaves: Reset Endpoint is refused (context state
        /// error), and Stop Endpoint takes the abandoned TRB back instead - left
        /// queued, its late completion would be collected by the next command
        /// and every command after it would read its neighbour's answer. Then
        /// Set TR Dequeue to where the next TRB will be written. Transfer events
        /// still in the ring go to their owners (the serial port and keyboard
        /// can share the slot), the stick's own stale ones are dropped.
        /// The device side, the halt feature, is the caller's: CLEAR_FEATURE.
        /// </remarks>
        public bool TryResetBulkEndpoint(uint slotId, bool directionIn)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;
            ref Device d = ref _devices[di];
            ref BulkEp ep = ref directionIn ? ref d.BulkIn : ref d.BulkOut;
            if (ep.Ring == 0) return false;
            uint epid = ep.Dci << 16;

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = 0; trb[1] = 0; trb[2] = 0;
            trb[3] = (TRB_RESET_ENDPOINT << 10) | epid | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            if (!TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _)) return false;

            if (code != 1)
            {
                trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
                trb[0] = 0; trb[1] = 0; trb[2] = 0;
                trb[3] = (TRB_STOP_ENDPOINT << 10) | epid | (slotId << 24) | _cmdCycle;
                AdvanceCommandRing();
                Write32(_doorbellBase, 0);
                if (!TryWaitEvent(TRB_CMD_COMPLETE, 1000, out _, out _)) return false;
                TryWaitEvent(0x3F, 0, 0, out _, out _);

                // Stopped keeps the data toggle, and CLEAR_FEATURE is about to
                // put the device's back to DATA0: on USB 2 the next packet would
                // be taken for a repeat and dropped. Only Reset Endpoint clears
                // the host's, and only from Halted - so the endpoint is dropped
                // and added again, on a clean ring, as Linux does.
                return TryReAddBulkEndpoint(ref d, slotId, ref ep, directionIn);
            }

            // A type no event has: everything in the ring is read through,
            // other functions' completions stashed for them, ours dropped.
            TryWaitEvent(0x3F, 0, 0, out _, out _);

            ulong resume = ep.Ring + ep.Enqueue * (ulong)TrbSize;
            trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)(resume | (ep.Cycle & 1));
            trb[1] = (uint)(resume >> 32);
            trb[2] = 0;
            trb[3] = (TRB_SET_TR_DEQUEUE << 10) | epid | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out code, out _) && code == 1;
        }

        // Configure Endpoint with the endpoint in both the drop and the add
        // flags: the controller forgets it and builds it again from the
        // input context, toggle and sequence number included. The slot
        // context is the current one, so nothing else on the slot changes.
        private bool TryReAddBulkEndpoint(ref Device d, uint slotId, ref BulkEp ep, bool directionIn)
        {
            for (int i = 0; i < RingTrbs * TrbSize; i++) ((byte*)ep.Ring)[i] = 0;
            ep.Enqueue = 0;
            ep.Cycle = 1;

            uint cs = ContextSize;
            ulong input = d.InputContext;
            for (uint i = 0; i < cs * (ep.Dci + 2); i++) ((byte*)input)[i] = 0;

            uint* icc = (uint*)input;
            icc[0] = 1u << (int)ep.Dci;                    // drop
            icc[1] = (1u << (int)ep.Dci) | 1;              // add, and the slot

            uint* slotOut = (uint*)d.DeviceContext;
            uint* slotIn = (uint*)(input + cs);
            for (int i = 0; i < 4; i++) slotIn[i] = slotOut[i];
            slotIn[3] = 0;                                 // state and address are the controller's

            WriteEpContext(input + cs * (ep.Dci + 1), directionIn ? EPTYPE_BULK_IN : EPTYPE_BULK_OUT,
                           ep.MaxPacket, ep.Ring);

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)input;
            trb[1] = (uint)(input >> 32);
            trb[2] = 0;
            trb[3] = (TRB_CONFIGURE_ENDPOINT << 10) | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _) && code == 1;
        }

        /// <summary>Endpoint address of the stick's bulk pipe, for CLEAR_FEATURE.</summary>
        public byte BulkAddress(uint slotId, bool directionIn)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return 0;
            return directionIn ? _devices[di].BulkIn.Address : _devices[di].BulkOut.Address;
        }

        /// <summary>The stick's interface number, for the class reset.</summary>
        public byte MassStorageInterface(uint slotId)
        {
            int di = IndexOfSlot(slotId);
            return di < 0 ? (byte)0 : _devices[di].MsdInterface;
        }

        private bool TryResetControlEndpoint(uint slotId)
        {
            int di = IndexOfSlot(slotId);
            if (di < 0) return false;

            ref Device d = ref _devices[di];

            uint* trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = 0;
            trb[1] = 0;
            trb[2] = 0;
            trb[3] = (TRB_RESET_ENDPOINT << 10) | EPID_CONTROL | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            if (!TryWaitEvent(TRB_CMD_COMPLETE, 1000, out uint code, out _) || code != 1)
                return false;

            // Resume where the next transfer will be written, not where the
            // stalled one sits. The cycle bit travels in bit 0 of the pointer:
            // it is what tells the controller which entries are ours.
            ulong resume = d.TransferRing + d.TrEnqueue * (ulong)TrbSize;

            trb = (uint*)(_cmdRing + _cmdEnqueue * TrbSize);
            trb[0] = (uint)(resume | (d.TrCycle & 1));
            trb[1] = (uint)(resume >> 32);
            trb[2] = 0;
            trb[3] = (TRB_SET_TR_DEQUEUE << 10) | EPID_CONTROL | (slotId << 24) | _cmdCycle;
            AdvanceCommandRing();
            Write32(_doorbellBase, 0);
            return TryWaitEvent(TRB_CMD_COMPLETE, 1000, out code, out _) && code == 1;
        }
    }
}
