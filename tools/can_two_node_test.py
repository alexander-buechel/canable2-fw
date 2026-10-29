#!/usr/bin/env python3
"""Hardware test: send an SLCAN frame between two CANable2 devices on one bus.

Usage: can_two_node_test.py [PORT_A] [PORT_B] [BITRATE_CODE]
Defaults: /dev/ttyACM0 /dev/ttyACM1 4 (S4 = 125 kbit/s). Needs pyserial.
Exit code 0 if the frame arrived in both directions, 1 otherwise.
"""
import sys
import time

import serial

FRAME = b"t1238DEADBEEF00112233\r"


def open_node(port, bitrate):
    s = serial.Serial(port, 115200, timeout=0.2)
    s.dtr = s.rts = True
    time.sleep(0.3)
    # close, bitrate, normal mode, autoretry, open (the firmware does not ack these)
    for cmd in (b"C\r", b"S%s\r" % bitrate.encode(), b"M0\r", b"A1\r", b"O\r"):
        s.write(cmd)
        s.flush()
        time.sleep(0.1)
    s.reset_input_buffer()
    return s


def send_and_check(tx, rx, name):
    tx.write(FRAME)
    tx.flush()
    time.sleep(0.5)
    got = rx.read(512)
    ok = FRAME.strip() in got
    print(f"{name}: sent {FRAME!r} rx {got!r} {'OK' if ok else 'FAIL'}")
    return ok


def main():
    port_a = sys.argv[1] if len(sys.argv) > 1 else "/dev/ttyACM0"
    port_b = sys.argv[2] if len(sys.argv) > 2 else "/dev/ttyACM1"
    bitrate = sys.argv[3] if len(sys.argv) > 3 else "4"
    a, b = open_node(port_a, bitrate), open_node(port_b, bitrate)
    try:
        ok = send_and_check(a, b, f"{port_a} -> {port_b}")
        ok &= send_and_check(b, a, f"{port_b} -> {port_a}")
        for s in (a, b):
            s.write(b"E\r")
            time.sleep(0.2)
            print(s.port, s.read(64))
    finally:
        for s in (a, b):
            s.write(b"C\r")
            s.close()
    return 0 if ok else 1


if __name__ == "__main__":
    sys.exit(main())
