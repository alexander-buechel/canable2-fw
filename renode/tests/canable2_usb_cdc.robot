*** Settings ***
Suite Setup       Setup
Suite Teardown    Teardown
Test Setup        Reset Emulation
Test Teardown     Test Teardown
Resource          ${RENODEKEYWORDS}
Resource          ${CURDIR}/canable2.resource

*** Test Cases ***
Firmware Boots And Blinks The Blue LED
    [Documentation]    led_blue_blink(2) in main(): 100 ms on, 100 ms off, twice, right after peripheral init.
    Create CANable2
    Wait For Blue LED    True
    # Now just after the first rising edge: step through the pattern in virtual time.
    Run Virtual Time    0.15
    Blue LED Should Be    False
    Run Virtual Time    0.1
    Blue LED Should Be    True
    Run Virtual Time    0.1
    Blue LED Should Be    False

Device Connects By Enabling The DP Pullup
    Create CANable2    auto_enumerate=False
    Run Virtual Time    0.2
    ${connected}=    Usb Property    Connected
    Should Be Equal    ${connected}    True
    ${configured}=    Usb Property    Configured
    Should Be Equal    ${configured}    False

Device Enumerates As A CDC Serial Port
    Create CANable2
    Wait For Enumeration

    ${vid}=    Usb Property    VendorId
    ${pid}=    Usb Property    ProductId
    Should Be Equal As Integers    ${vid}    0x16D0
    Should Be Equal As Integers    ${pid}    0x117E

    ${address}=    Usb Property    DeviceAddress
    Should Be Equal As Integers    ${address}    2
    ${config}=    Usb Property    ConfigurationValue
    Should Be Equal As Integers    ${config}    1
    ${cdc}=    Usb Property    HasCdcInterface
    Should Be Equal    ${cdc}    True

    ${manufacturer}=    Usb Property    Manufacturer
    Should Be Equal    ${manufacturer}    Openlight Labs
    ${product}=    Usb Property    Product
    Should Start With    ${product}    CANable2

Device Exposes CDC Bulk And Notification Endpoints
    Create CANable2
    Wait For Enumeration
    ${bulk_in}=    Usb Property    BulkInEndpoint
    ${bulk_out}=    Usb Property    BulkOutEndpoint
    ${notify}=    Usb Property    NotificationEndpoint
    ${max_packet}=    Usb Property    BulkMaxPacketSize
    Should Be Equal As Integers    ${bulk_in}    0x81
    Should Be Equal As Integers    ${bulk_out}    0x01
    Should Be Equal As Integers    ${notify}    0x82
    Should Be Equal As Integers    ${max_packet}    64

Line Coding And Control Lines Are Negotiated
    Create CANable2
    Wait For Enumeration
    ${baud}=    Usb Property    BaudRate
    Should Be Equal As Integers    ${baud}    115200
    ${lines}=    Usb Property    ControlLineState
    Should Be Equal As Integers    ${lines}    3

Device Re-Enumerates After A Bus Reset
    Create CANable2
    Wait For Enumeration
    ${before}=    Usb Property    EnumerationCount
    Execute Command    sysbus.usb BusReset
    Run Virtual Time    0.005
    ${state}=    Usb Property    HostState
    Should Not Be Equal    ${state}    Configured
    Wait For Enumeration
    ${after}=    Usb Property    EnumerationCount
    ${expected}=    Evaluate    int(${before}) + 1
    Should Be Equal As Integers    ${after}    ${expected}

Version Command Returns Firmware Id Over CDC
    Create CANable2
    Create Terminal Tester    sysbus.usb    defaultPauseEmulation=True
    Start Emulation
    Wait For Enumeration While Running
    Send SLCAN    V
    # fw_id is "<git describe> <git remote>\r": terminated by CR only, so wait for it as a prompt, not as a line
    Wait For Prompt On Uart    github.com    timeout=5

Several Commands Are Answered In Order
    Create CANable2
    Create Terminal Tester    sysbus.usb    defaultPauseEmulation=True
    Start Emulation
    Wait For Enumeration While Running
    Send SLCAN    S4
    Send SLCAN    M0
    Send SLCAN    V
    Wait For Prompt On Uart    github.com    timeout=5
    Send SLCAN    E
    Wait For Prompt On Uart    CANable Error Register: 0    timeout=5

Long Command Stream Spanning Multiple USB Packets Is Handled
    [Documentation]    Twenty commands sent back to back, each in its own OUT packet, followed by a query.
    Create CANable2
    Create Terminal Tester    sysbus.usb    defaultPauseEmulation=True
    Start Emulation
    Wait For Enumeration While Running
    Wait For Firmware Main Loop
    FOR    ${i}    IN RANGE    20
        Send SLCAN    S4
    END
    Send SLCAN    V
    Wait For Prompt On Uart    github.com    timeout=5

Open And Close Channel Do Not Break The Port
    [Documentation]    FDCAN is not modelled, but the firmware must keep serving USB while the channel is opened and closed.
    Create CANable2
    Create Terminal Tester    sysbus.usb    defaultPauseEmulation=True
    Start Emulation
    Wait For Enumeration While Running
    Send SLCAN    S6
    Send SLCAN    O
    Send SLCAN    C
    Send SLCAN    V
    Wait For Prompt On Uart    github.com    timeout=5

Device Stays Responsive After Re-Enumeration
    Create CANable2
    Create Terminal Tester    sysbus.usb    defaultPauseEmulation=True
    Start Emulation
    Wait For Enumeration While Running
    Send SLCAN    V
    Wait For Prompt On Uart    github.com    timeout=5
    Execute Command    sysbus.usb BusReset
    Wait For Enumeration While Running
    Send SLCAN    V
    Wait For Prompt On Uart    github.com    timeout=5
