*** Settings ***
Documentation       Shared keywords for STM32U575 tests.
Library             String

*** Variables ***
${ROOT}             ${CURDIR}/../..
${PLATFORM}         ${ROOT}/platforms/cpus/stm32u575.repl
${PLATFORM_WITH_OSCILLATORS}    ${CURDIR}/stm32u575-with-oscillators.repl

*** Keywords ***
Create STM32U575 Machine
    [Arguments]                     ${platform}=${PLATFORM}
    Execute Command                 include @${ROOT}/scripts/load-models.resc
    Execute Command                 mach create "stm32u575"
    Execute Command                 machine LoadPlatformDescription @${platform}

Create STM32U575 Machine With External Oscillators
    Create STM32U575 Machine        ${PLATFORM_WITH_OSCILLATORS}

Write Word
    [Arguments]                     ${address}    ${value}
    ${address}=                     Evaluate           hex(int(${address}))
    ${value}=                       Evaluate           int(${value})
    ${value}=                       Convert To Hex     ${value}    prefix=0x
    Execute Command                 sysbus WriteDoubleWord ${address} ${value}

Read Word
    [Arguments]                     ${address}
    ${address}=                     Evaluate           hex(int(${address}))
    ${value}=                       Execute Command    sysbus ReadDoubleWord ${address}
    ${value}=                       Strip String       ${value}
    ${value}=                       Convert To Integer    ${value}
    RETURN                          ${value}

Word Should Be
    [Arguments]                     ${address}    ${expected}
    ${expected}=                    Evaluate           int(${expected})
    ${value}=                       Read Word          ${address}
    ${hex}=                         Convert To Hex     ${value}    prefix=0x    length=8
    Should Be Equal As Integers     ${value}           ${expected}    msg=${address} reads ${hex}    values=False

Bits Should Be Set
    [Arguments]                     ${address}    ${mask}
    ${value}=                       Read Word          ${address}
    ${hex}=                         Convert To Hex     ${value}    prefix=0x    length=8
    ${mask}=                        Evaluate           int(${mask})
    ${masked}=                      Evaluate           ${value} & ${mask}
    ${maskhex}=                     Convert To Hex     ${mask}    prefix=0x    length=8
    Should Be Equal As Integers     ${masked}          ${mask}    msg=${address} reads ${hex}, expected bits ${maskhex} set    values=False

Bits Should Be Clear
    [Arguments]                     ${address}    ${mask}
    ${value}=                       Read Word          ${address}
    ${hex}=                         Convert To Hex     ${value}    prefix=0x    length=8
    ${mask}=                        Evaluate           int(${mask})
    ${masked}=                      Evaluate           ${value} & ${mask}
    ${maskhex}=                     Convert To Hex     ${mask}    prefix=0x    length=8
    Should Be Equal As Integers     ${masked}          0    msg=${address} reads ${hex}, expected bits ${maskhex} clear    values=False

Field Should Be
    [Arguments]                     ${address}    ${position}    ${width}    ${expected}
    ${value}=                       Read Word          ${address}
    ${field}=                       Evaluate           (${value} >> ${position}) & ((1 << ${width}) - 1)
    Should Be Equal As Integers     ${field}           ${expected}    msg=${address} field at bit ${position}, width ${width}    values=False

Property Should Be
    [Arguments]                     ${command}    ${expected}
    ${value}=                       Execute Command    ${command}
    ${value}=                       Strip String       ${value}
    Should Be Equal As Integers     ${value}           ${expected}    msg=${command} = ${value}    values=False

Interrupt Line Should Be
    [Arguments]                     ${peripheral}    ${expected}
    ${state}=                       Execute Command    ${peripheral} IRQ IsSet
    ${state}=                       Strip String       ${state}
    Should Be Equal                 ${state}           ${expected}
