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
        private void AbortEndpointTransfer(uint slotId, uint dci, ulong ring, uint enqueue, uint cycle)
        {
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
