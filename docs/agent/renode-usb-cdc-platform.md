# Renode platform and USB CDC peripheral

Summary of the work that added a Renode simulation of the CANable 2.0 (STM32G431CB), a new USB device
peripheral, and Robot Framework tests. Details and usage live in `renode/README.md`.

## What was added

- `renode/peripherals/STM32G4_USB.cs`: USB FS device controller (RM0440 ch. 45) with a built-in virtual host.
  - Device side: `USB_EPnR` (toggle / write-0-to-clear semantics), `CNTR`, `ISTR`, `DADDR`, `BTABLE`, `BCDR`,
    packet memory in a separate `ArrayMemory` at 0x40006000, IRQ on USB_LP (NVIC 20).
  - Host side: 1 kHz frame timer. On `BCDR.DPPU` it resets the bus and enumerates (device/config/string
    descriptors, SET_ADDRESS, SET_CONFIGURATION, CDC line coding and control line state). Afterwards the bulk
    endpoints are bridged to the Renode `IUART` interface.
- `renode/peripherals/STM32G4_RCC.cs`, `STM32G4_FlashController.cs`: minimal models with the ready flags,
  unlock keys and option register (BOR level 4) the firmware polls.
- `renode/stm32g431.repl`, `canable2.repl` (LEDs), `canable2.resc` (interactive, pty at `/tmp/canable2-cdc`).
- `renode/tests/canable2_usb_cdc.robot` + `canable2.resource`: 11 tests (boot LED, connect, enumeration,
  endpoints, line coding, re-enumeration, SLCAN `V`/`E`/`S`/`M`/`O`/`C`, burst of 20 commands).
- `renode/run-tests.sh`: runs the tests; `ROBOT_DEPS` (default `~/.local/lib/renode-test-deps`) holds the
  unpacked Robot Framework wheels because the machine has no pip.
- `Makefile`: one line symlinks `build/canable2.elf` to the git-hash-named ELF. `.gitignore`: Renode test output.

## Status

All 11 tests pass; the suite was stable over four consecutive full runs. Interactive use over the pty was
verified with `V`, `S4`, `E`. Nothing is committed.

## Findings and limitations

- Firmware: `main()` blocks ~400 ms in `led_blue_blink(2)` while USB is interrupt driven into an 8-slot RX ring.
  More than 8 packets in that window overflow it (`ERR_FULLBUF_USBRX`, error register bit 5). Tests wait for the
  main loop (`Wait For Firmware Main Loop`) before sending bursts.
- Firmware replies end in `\r`, not `\n`, so tests use `Wait For Prompt On Uart` rather than `Wait For Line`.
- Robot gotchas: `emulation RunFor` is unavailable once the emulation is started; traffic tests use
  `Wait For Enumeration While Running` (log tester) instead. `$ORIGIN` is not supported in `.resc` here, so the
  launcher uses repo-root-relative paths.
- Not modelled: FDCAN (register storage only, no frames on any bus), double-buffered/isochronous endpoints,
  suspend/resume, LPM, battery charging detection. Unique device ID reads as zero.
- Possible next step: an FDCAN bus model to test the `t`/`T` SLCAN frame commands.
