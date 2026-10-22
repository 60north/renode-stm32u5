*** Settings ***
Documentation       STM32U575 core platform: memory map, boot, interrupts and SysTick.
...                 Firmware: firmware-tests/baremetal/core (build with CMake before running).
Library             String
Test Setup          Create STM32U575 Machine

*** Variables ***
${ROOT}             ${CURDIR}/../..
${PLATFORM}         ${ROOT}/platforms/cpus/stm32u575.repl
${ELF}              ${ROOT}/build/firmware/core-smoke.elf

# Result bits written by the firmware to passedChecks (see main.c).
${CHECK_DATA}       0x00000001
${CHECK_BSS}        0x00000002
${CHECK_FLASH}      0x00000004
${CHECK_SRAM1}      0x00000008
${CHECK_SRAM2}      0x00000010
${CHECK_SRAM3}      0x00000020
${CHECK_SRAM4}      0x00000040
${CHECK_IRQ_0}      0x00000080
${CHECK_IRQ_125}    0x00000100
${CHECK_DONE}       0x80000000

*** Keywords ***
Create STM32U575 Machine
    Execute Command                 include @${ROOT}/scripts/load-models.resc
    Execute Command                 mach create "stm32u575"
    Execute Command                 machine LoadPlatformDescription @${PLATFORM}

Load Core Firmware
    Execute Command                 $bin=@${ELF}
    Execute Command                 macro reset "sysbus LoadELF $bin"
    Execute Command                 runMacro $reset

Read Word
    [Arguments]                     ${address}
    ${value}=                       Execute Command    sysbus ReadDoubleWord ${address}
    ${value}=                       Strip String       ${value}
    ${value}=                       Convert To Integer    ${value}
    RETURN                          ${value}

Read Symbol
    [Arguments]                     ${symbol}
    ${address}=                     Execute Command    sysbus GetSymbolAddress "${symbol}"
    ${address}=                     Strip String       ${address}
    ${value}=                       Read Word          ${address}
    RETURN                          ${value}

Check Should Pass
    [Arguments]                     ${flag}
    ${passed}=                      Read Symbol        passedChecks
    ${flag}=                        Convert To Integer    ${flag}
    ${masked}=                      Evaluate           ${passed} & ${flag}
    Should Be Equal As Integers     ${masked}          ${flag}    msg=passedChecks=${passed}

Memory Should Hold Written Word
    [Arguments]                     ${address}
    Execute Command                 sysbus WriteDoubleWord ${address} 0xA5C3F00F
    ${value}=                       Read Word          ${address}
    Should Be Equal As Integers     ${value}           0xA5C3F00F    msg=address ${address}

*** Test Cases ***
Should Map All Memories At Documented Addresses
    [Documentation]                 First and last word of each region (stm32u575xx.h, RM0456 memory map).
    FOR    ${address}    IN
    ...    0x08000000    0x081FFFFC
    ...    0x20000000    0x2002FFFC
    ...    0x20030000    0x2003FFFC
    ...    0x20040000    0x200BFFFC
    ...    0x28000000    0x28003FFC
    ...    0x40036400    0x40036BFC
    ...    0x0BFA0000    0x0BFA01FC
        Memory Should Hold Written Word    ${address}
    END

Should Report 2 MB In Flash Size Data
    ${value}=                       Execute Command    sysbus ReadWord 0x0BFA07A0
    ${value}=                       Strip String       ${value}
    Should Be Equal As Integers     ${value}           0x0800

Should Boot From Flash Vector Table
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    # InitVectorTableOffset is applied when the core starts, not at platform load.
    ${vtor}=                        Execute Command    cpu VectorTableOffset
    ${vtor}=                        Strip String       ${vtor}
    Should Be Equal As Integers     ${vtor}            0x08000000
    ${resets}=                      Read Symbol        resetHandlerCount
    Should Be Equal As Integers     ${resets}          1
    Check Should Pass               ${CHECK_DONE}

Should Initialize Data And Bss
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    Check Should Pass               ${CHECK_DATA}
    Check Should Pass               ${CHECK_BSS}

Should Read Constants And Flash Size From Firmware
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    Check Should Pass               ${CHECK_FLASH}
    ${size}=                        Read Symbol        flashSizeKb
    Should Be Equal As Integers     ${size}            2048

Should Write And Read All SRAM Banks From Firmware
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    Check Should Pass               ${CHECK_SRAM1}
    Check Should Pass               ${CHECK_SRAM2}
    Check Should Pass               ${CHECK_SRAM3}
    Check Should Pass               ${CHECK_SRAM4}

Should Dispatch First And Last Interrupt Lines
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    Check Should Pass               ${CHECK_IRQ_0}
    Check Should Pass               ${CHECK_IRQ_125}
    ${count}=                       Read Symbol        irqLogCount
    Should Be Equal As Integers     ${count}           2

Should Generate SysTick At 1 kHz From Reset Clock
    Load Core Firmware
    Execute Command                 emulation RunFor "0.1"
    ${ticks}=                       Read Symbol        sysTickCount
    Should Be Equal As Integers     ${ticks}           100

Should Keep Executing Main Loop
    Load Core Firmware
    Execute Command                 emulation RunFor "0.01"
    ${first}=                       Read Symbol        loopCount
    Execute Command                 emulation RunFor "0.01"
    ${second}=                      Read Symbol        loopCount
    Should Be True                  ${second} > ${first}
    ${faults}=                      Read Symbol        faultCount
    Should Be Equal As Integers     ${faults}          0

Should Restart Firmware After Machine Reset
    Load Core Firmware
    Execute Command                 emulation RunFor "0.05"
    Execute Command                 machine Reset
    Execute Command                 emulation RunFor "0.05"
    # 50 ticks, not 100: the counter was cleared by the restarted start-up code.
    ${ticks}=                       Read Symbol        sysTickCount
    Should Be Equal As Integers     ${ticks}           50
    Check Should Pass               ${CHECK_DONE}
