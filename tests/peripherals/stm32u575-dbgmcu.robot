*** Settings ***
Documentation       STM32U5 DBGMCU registers without firmware (RM0456 Rev 7 section 75.12).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Halted Machine

*** Variables ***
${DBGMCU}           0xE0044000

*** Keywords ***
Create Halted Machine
    Create STM32U575 Machine
    # No firmware: the core is halted.
    Execute Command                 cpu IsHalted true

*** Test Cases ***
Should Identify The Device
    # DEV_ID 0x482 (STM32U575/585), REV_ID 0x3001 (revision W): SVD reset value of DBGMCU_IDCODE.
    Word Should Be                  ${DBGMCU} + 0x00   0x30016482
    Write Word                      ${DBGMCU} + 0x00   0
    Word Should Be                  ${DBGMCU} + 0x00   0x30016482
    Word Should Be                  ${DBGMCU} + 0xFC   0x00000001

Should Store The Debug Configuration And Freeze Bits
    # DBG_STOP, DBG_STANDBY; DBG_IWDG_STOP (APB1LFZR bit 12).
    Write Word                      ${DBGMCU} + 0x04   0x00000006
    Word Should Be                  ${DBGMCU} + 0x04   0x00000006
    Write Word                      ${DBGMCU} + 0x08   0x00001000
    Word Should Be                  ${DBGMCU} + 0x08   0x00001000
