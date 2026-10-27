*** Settings ***
Documentation       Unmodified STM32CubeU5 HAL (v1.9.0 components) with timers (firmware-tests/stm32cube/tim):
...                 TIM6 as the HAL time base from the HAL template, reconfigured for 160 MHz, and
...                 TIM4 channel 4 generating a 1 Hz square wave in PWM mode 1, observed on an LED
...                 connected to the channel output (tests/common/stm32u575-tim-fixture.repl).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Run HAL Timer Firmware

*** Variables ***
${ELF}              ${ROOT}/build/firmware/hal-tim.elf
${FIXTURE}          ${CURDIR}/../common/stm32u575-tim-fixture.repl
${TIM4}             0x40000800
${TIM2}             0x40000000
${TIM3}             0x40000400
${STEP_DONE}        6
${UNMODELLED_ACCESS}    Unhandled (read|write)|non existing peripheral

*** Keywords ***
Run HAL Timer Firmware
    Create STM32U575 Machine        ${FIXTURE}
    Create Log Tester               0
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset
    Execute Command                 emulation RunFor "0.05"

Read Symbol
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Symbol Should Be
    [Arguments]                     ${symbol}    ${expected}
    ${value}=                       Read Symbol        ${symbol}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${symbol}

*** Test Cases ***
Should Run HAL Delay On The TIM6 Time Base
    Symbol Should Be                failedStep         0
    Symbol Should Be                delayDone          1
    Symbol Should Be                step               ${STEP_DONE}
    # TIM6 is clocked by PCLK1 = 160 MHz (APB1 prescaler 1, RM0456 section 11.4.15).
    ${frequency}=                   Execute Command    sysbus.tim6 Frequency
    Should Be Equal As Integers     ${frequency.strip()}    160000000

Should Count One HAL Tick Per Millisecond
    ${start}=                       Read Symbol        uwTick
    Execute Command                 emulation RunFor "0.1"
    ${end}=                         Read Symbol        uwTick
    ${ticks}=                       Evaluate           ${end} - ${start}
    Should Be True                  99 <= ${ticks} <= 101    msg=${ticks} ticks in 100 ms

Should Configure TIM4 Channel 4 For 1 Hz PWM
    # PSC = 160 MHz / 10 kHz - 1, ARR = 10000 - 1, CCR4 = 5000.
    Word Should Be                  ${TIM4} + 0x28     15999
    Word Should Be                  ${TIM4} + 0x2C     9999
    Word Should Be                  ${TIM4} + 0x40     5000

Should Drive TIM4 Channel 4 High Before First Overflow
    ${led}=                         Create LED Tester    sysbus.tim4.tim4ch4    defaultTimeout=2
    Assert LED State                true    timeout=0    testerId=${led}

Should Output A 1 Hz Square Wave On TIM4 Channel 4
    ${led}=                         Create LED Tester    sysbus.tim4.tim4ch4    defaultTimeout=2
    Assert LED Is Blinking          testDuration=3    onDuration=0.5    offDuration=0.5    tolerance=0.1    testerId=${led}

Should Apply Preloaded Compare On Update Event
    ${led}=                         Create LED Tester    sysbus.tim4.tim4ch4    defaultTimeout=2
    Write Word                      ${TIM4} + 0x40     0
    Word Should Be                  ${TIM4} + 0x40     0
    Assert LED State                true    timeout=0    testerId=${led}
    Write Word                      ${TIM4} + 0x14     1
    Assert LED State                false   timeout=0    testerId=${led}

Should Apply Preloaded Auto Reload On Update Event
    Write Word                      ${TIM4} + 0x2C     4999
    Write Word                      ${TIM4} + 0x40     2500
    Write Word                      ${TIM4} + 0x14     1
    ${led}=                         Create LED Tester    sysbus.tim4.tim4ch4    defaultTimeout=2
    Assert LED Is Blinking          testDuration=2    onDuration=0.25    offDuration=0.25    tolerance=0.05    testerId=${led}

Should Apply TIM2 Sound Period And Duty Change
    # 32-bit timer, PWM1, preloads and UG sequence of a buzzer driver, slowed for LED observation.
    Property Should Be              sysbus.tim2 Frequency    160000000
    Write Word                      ${TIM2} + 0x00     128
    Write Word                      ${TIM2} + 0x28     15999
    Write Word                      ${TIM2} + 0x2C     9999
    Write Word                      ${TIM2} + 0x18     104
    Write Word                      ${TIM2} + 0x34     5000
    Write Word                      ${TIM2} + 0x14     1
    Write Word                      ${TIM2} + 0x20     1
    Write Word                      ${TIM2} + 0x00     129
    ${led}=                         Create LED Tester    sysbus.tim2.tim2ch1    defaultTimeout=2
    Assert LED State                true    timeout=0    testerId=${led}
    Write Word                      ${TIM2} + 0x2C     4999
    Write Word                      ${TIM2} + 0x34     1250
    Write Word                      ${TIM2} + 0x14     1
    Assert LED Is Blinking          testDuration=2    onDuration=0.125    offDuration=0.375    tolerance=0.05    testerId=${led}
    Execute Command                 pause
    Write Word                      ${TIM2} + 0x34     0
    Execute Command                 emulation RunFor "0.6"
    Assert LED State                false    timeout=0    testerId=${led}

Should Stop TIM3 One Shot After One Update
    Write Word                      ${TIM3} + 0x28     15999
    Write Word                      ${TIM3} + 0x2C     2499
    Write Word                      ${TIM3} + 0x14     1
    Write Word                      ${TIM3} + 0x10     0
    Write Word                      ${TIM3} + 0x00     9
    Execute Command                 emulation RunFor "0.3"
    Bits Should Be Set              ${TIM3} + 0x10     1
    Bits Should Be Clear            ${TIM3} + 0x00     1

Should Block The Counter While ARR Is Zero
    # RM0456: the counter is blocked while the auto-reload value is null.
    Write Word                      ${TIM3} + 0x2C     0
    Write Word                      ${TIM3} + 0x10     0
    Write Word                      ${TIM3} + 0x00     1
    Execute Command                 emulation RunFor "0.01"
    Word Should Be                  ${TIM3} + 0x24     0
    Bits Should Be Clear            ${TIM3} + 0x10     1

Should Not Touch Unmodelled Registers
    Should Not Be In Log            ${UNMODELLED_ACCESS}    treatAsRegex=true
