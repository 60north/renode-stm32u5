*** Settings ***
Documentation       STM32U5 RNG register behaviour without firmware (RM0456 Rev 7 section 48.7).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Halted Machine

*** Variables ***
${RNG}              0x420C0800

*** Keywords ***
Create Halted Machine
    Create STM32U575 Machine
    Execute Command                 cpu IsHalted true

*** Test Cases ***
Should Have The Documented Reset Values
    # RNG_CR 0x0080 0D00, RNG_SR 0, RNG_NSCR 0x0003 FFFF, RNG_HTCR 0x0000 72AC.
    Word Should Be                  ${RNG} + 0x00      0x00800D00
    Word Should Be                  ${RNG} + 0x04      0x00000000
    Word Should Be                  ${RNG} + 0x0C      0x0003FFFF
    Word Should Be                  ${RNG} + 0x10      0x000072AC

Should Give Random Data Only While Enabled
    Word Should Be                  ${RNG} + 0x08      0x00000000
    # RNGEN keeping the reset configuration.
    Write Word                      ${RNG} + 0x00      0x00800D04
    Bits Should Be Set              ${RNG} + 0x04      0x1
    ${first}=                       Read Word          ${RNG} + 0x08
    ${second}=                      Read Word          ${RNG} + 0x08
    Should Not Be Equal As Integers    ${first}       ${second}

Should Freeze The Configuration With CONFIGLOCK
    # CONFIGLOCK with CLKDIV = 3: later configuration writes are ignored until reset, RNGEN
    # still works.
    Write Word                      ${RNG} + 0x00      0x80830D00
    Write Word                      ${RNG} + 0x00      0x00000D04
    Word Should Be                  ${RNG} + 0x00      0x80830D04
    Write Word                      ${RNG} + 0x0C      0x00000000
    Word Should Be                  ${RNG} + 0x0C      0x0003FFFF
    Execute Command                 machine Reset
    Execute Command                 cpu IsHalted true
    Word Should Be                  ${RNG} + 0x00      0x00800D00
