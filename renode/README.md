# CANable 2.0 in Renode

Runs the unmodified firmware (`build/canable2*.elf`) on a simulated STM32G431CB and connects it to a simulated
USB host, so the USB CDC (virtual COM port) / SLCAN interface can be developed and tested without hardware.

```
renode/
  canable2.repl            board: STM32G431 + the two LEDs
  stm32g431.repl           SoC: memories, NVIC, GPIO, RCC, flash, USB (+PMA), stubs for FDCAN/PWR/CRS
  canable2.resc            interactive launch script
  peripherals/
    STM32G4_USB.cs         USB FS device controller + virtual host + CDC bridge (the interesting part)
    STM32G4_RCC.cs         clock control with the ready/switch-status handshakes the HAL waits for
    STM32G4_FlashController.cs   ACR / unlock keys / option register
  tests/
    canable2.resource      shared keywords
    canable2_usb_cdc.robot Robot Framework tests
  run-tests.sh             runs the tests
```

## Interactive use

```
make
renode renode/canable2.resc          # then `start`
picocom /tmp/canable2-cdc            # SLCAN over the CDC port, commands end with CR: V<CR>, S4<CR>, O<CR> ...
```

The CDC port is exposed as a pty (`$pty`, default `/tmp/canable2-cdc`). Useful monitor commands:

```
sysbus.usb HostState         # Idle / Attached / Resetting / Enumerating / Configured / Failed
sysbus.usb VendorId          # parsed from the descriptors the virtual host read
sysbus.usb BusReset          # unplug/replug: reset the bus and enumerate again
logLevel -1 sysbus.usb       # show every USB transaction (SETUP/OUT/IN with payload)
```

## Tests

```
make
renode/run-tests.sh                      # everything
renode/run-tests.sh -f "Version*"        # tests whose name matches
renode/run-tests.sh --variable ELF:/path/to/firmware.elf
```

Needs Renode (`renode` in `PATH` or `RENODE_ROOT`) and Robot Framework 6.x. If `robot` is not installed
system-wide, unpack the wheels into a directory and point `ROBOT_DEPS` at it (default
`~/.local/lib/renode-test-deps`); `run-tests.sh` adds it to `PYTHONPATH`.

## How the USB model works

`STM32G4_USB` has a device half and a host half that only communicate through the same registers and packet
memory the real peripheral exposes, so the ST HAL / USB middleware run exactly as on hardware.

*Device half* (RM0440 chapter 45): `USB_EPnR` with its toggle / write-0-to-clear bit semantics, `CNTR`, `ISTR`
(CTR/DIR/EP_ID derived from the endpoint registers), `DADDR`, `BTABLE`, `BCDR`, and the 1 KB packet memory
(`usbPma`, an `ArrayMemory` right behind the registers) holding the buffer descriptor table and endpoint buffers.
Interrupts go to `USB_LP` (NVIC 20).

*Host half*: a 1 kHz frame timer. When the firmware sets `BCDR.DPPU` (`USB_DevConnect`) the host waits 10 ms,
issues a bus reset (`ISTR.RESET`), waits for reset recovery and enumerates like Linux does: device descriptor (8
bytes, then 18), `SET_ADDRESS`, configuration descriptor, strings, `SET_CONFIGURATION`, then the CDC
`SET_LINE_CODING`, `GET_LINE_CODING`, `SET_CONTROL_LINE_STATE`. Transfers are made of SETUP/IN/OUT transactions
that honour the endpoint STAT bits (NAK until the firmware arms the endpoint, STALL, DISABLED), update
`COUNT_RX`, toggle DTOG and set `CTR_RX`/`CTR_TX`. Once configured, the bulk endpoints found in the
configuration descriptor are polled every frame (up to 16 packets) and bridged to the Renode `IUART`
interface, so anything that attaches to a UART works: terminal testers in Robot, pty / socket terminals,
`uart` loggers.

Passing `autoEnumerate: false` in the platform (or `sysbus.usb AutoEnumerate false`) leaves enumeration
to `sysbus.usb Enumerate`.

### Limitations

* Double-buffered and isochronous endpoints, suspend/resume, LPM and battery charging detection are not modelled.
* `PDWN` start-up delay is not modelled, the pins are ideal.
* FDCAN1 is only register storage: `O`/`C`/`S` commands are accepted, but no frame is ever transmitted
  or received. A bus model for it would be the next step to test the `t`/`T` frame commands.
* The unique device ID reads as zero, so the USB serial number string is all zeros.

### Firmware behaviour worth knowing

`main()` calls `led_blue_blink(2)` before entering its loop, which blocks for ~400 ms. USB is interrupt
driven, so the device enumerates during that time, but the 8 slot receive ring (`NUM_RX_BUFS`) is not drained
until the loop starts. A host that sends more than 8 packets in that window overflows it (`ERR_FULLBUF_USBRX`,
bit 5 of the error register) and data is dropped. The tests wait for the main loop before sending bursts
(`Wait For Firmware Main Loop`).
