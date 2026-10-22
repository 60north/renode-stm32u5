*** Settings ***
Documentation       Toolchain smoke test. Verifies that the Renode installation used by this
...                 repository can build a Cortex-M33 machine, take SP and PC from a vector
...                 table on reset and execute Thumb code. It does not exercise any STM32U5
...                 model.
Library             String

*** Variables ***
${PLATFORM}         ${CURDIR}/cortex-m33-smoke.repl
${INITIAL_SP}       0x20001000
# Reset handler at 0x8 with the Thumb bit set.
${RESET_VECTOR}     0x00000009
# 0x202A: movs r0, #42    0xE7FE: b .
${PROGRAM}          0xE7FE202A

*** Keywords ***
Create Smoke Machine
    Execute Command                 mach create "smoke"
    Execute Command                 machine LoadPlatformDescription @${PLATFORM}
    Execute Command                 sysbus WriteDoubleWord 0x00000000 ${INITIAL_SP}
    Execute Command                 sysbus WriteDoubleWord 0x00000004 ${RESET_VECTOR}
    Execute Command                 sysbus WriteDoubleWord 0x00000008 ${PROGRAM}
    Execute Command                 cpu Reset

Register Should Be
    [Arguments]                     ${register}    ${expected}
    ${value}=                       Execute Command    cpu GetRegister ${register}
    ${value}=                       Strip String       ${value}
    Should Be Equal As Integers     ${value}           ${expected}

*** Test Cases ***
Should Load Stack Pointer And Entry Point From Vector Table
    Create Smoke Machine
    Execute Command                 cpu Step 1
    Register Should Be              13    ${INITIAL_SP}

Should Execute Code From Reset Handler
    Create Smoke Machine
    Execute Command                 cpu Step 2
    Register Should Be              0     42
    # The branch-to-self keeps the core on the loop instruction.
    Execute Command                 cpu Step 10
    Register Should Be              15    0x0000000A
