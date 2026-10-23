*** Settings ***
Library           OperatingSystem
Library           String

*** Variables ***
${RENODE_DIR}         ${CURDIR}/..
${REPL}               ${RENODE_DIR}/canable2.repl
${ELF}                ${EMPTY}
# Longest virtual time we are willing to wait for the host to finish enumerating
${ENUMERATION_TIMEOUT}    2

*** Keywords ***
Find Firmware
    [Documentation]    Use --variable ELF:<path> to pick a specific image, otherwise the newest build/canable2*.elf.
    IF    '${ELF}' != ''
        RETURN    ${ELF}
    END
    ${found}=    Evaluate    sorted(glob.glob(r'${RENODE_DIR}/../build/canable2*.elf'), key=os.path.getmtime)    modules=glob,os
    Should Not Be Empty    ${found}    No firmware image in build/, run `make` first
    RETURN    ${found}[-1]

Create CANable2
    [Documentation]    Build the machine. With auto_enumerate=False the host stays quiet after connecting until
    ...                `sysbus.usb Enumerate` is issued.
    [Arguments]    ${auto_enumerate}=True
    ${elf}=    Find Firmware
    Execute Command    mach create "canable2"
    Execute Command    include @${RENODE_DIR}/peripherals/STM32G4_USB.cs
    Execute Command    include @${RENODE_DIR}/peripherals/STM32G4_RCC.cs
    Execute Command    include @${RENODE_DIR}/peripherals/STM32G4_FlashController.cs
    Execute Command    machine LoadPlatformDescription @${REPL}
    Execute Command    sysbus.usb AutoEnumerate ${auto_enumerate}
    Execute Command    sysbus LoadELF @${elf}
    Create Log Tester    ${ENUMERATION_TIMEOUT}

Usb Property
    [Arguments]    ${name}
    ${value}=    Execute Command    sysbus.usb ${name}
    RETURN    ${value.strip()}

Run Virtual Time
    [Arguments]    ${seconds}
    Execute Command    emulation RunFor "${seconds}"

Wait For Enumeration While Running
    [Documentation]    For tests that have called `Start Emulation`: waits for the virtual host to configure the device.
    ...                Each call consumes one "device configured" log message, so it can be used again after a bus reset.
    [Arguments]    ${timeout}=${ENUMERATION_TIMEOUT}
    Wait For Log Entry    Host: device configured    timeout=${timeout}

Wait For Enumeration
    [Documentation]    For tests that drive time with `Run Virtual Time`: runs until the host has configured the device.
    [Arguments]    ${timeout}=${ENUMERATION_TIMEOUT}
    ${steps}=    Evaluate    int(float(${timeout}) / 0.01)
    FOR    ${i}    IN RANGE    ${steps}
        ${state}=    Usb Property    HostState
        IF    '${state}' == 'Configured'
            RETURN
        END
        IF    '${state}' == 'Failed'
            ${error}=    Usb Property    LastError
            Fail    USB enumeration failed: ${error}
        END
        Run Virtual Time    0.01
    END
    ${state}=    Usb Property    HostState
    Fail    USB device was not configured within ${timeout} s of virtual time (host state: ${state})

Send SLCAN
    [Documentation]    Sends a command terminated by CR over the CDC port.
    [Arguments]    ${command}
    Write To Uart    ${command}\r

Wait For Firmware Main Loop
    [Documentation]    main() blocks for about 400 ms in led_blue_blink(2) before it starts draining the USB receive
    ...                ring (8 packets). USB is serviced from interrupts meanwhile, so a host that sends faster than
    ...                that overflows the ring. Tests that send bursts wait here first.
    ...                Pauses the running emulation, advances virtual time and resumes.
    Execute Command    pause
    Execute Command    emulation RunFor "0.5"
    Execute Command    start

Blue LED State
    ${state}=    Execute Command    sysbus.gpioPortA.ledBlue State
    RETURN    ${state.strip()}

Blue LED Should Be
    [Arguments]    ${expected}
    ${state}=    Blue LED State
    Should Be Equal    ${state}    ${expected}

Wait For Blue LED
    [Documentation]    Steps virtual time in 1 ms increments until the blue LED has the given state.
    [Arguments]    ${expected}    ${timeout}=0.5
    ${steps}=    Evaluate    int(float(${timeout}) / 0.001)
    FOR    ${i}    IN RANGE    ${steps}
        ${state}=    Blue LED State
        IF    '${state}' == '${expected}'
            RETURN
        END
        Run Virtual Time    0.001
    END
    Fail    Blue LED did not become ${expected} within ${timeout} s of virtual time
