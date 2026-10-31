*** Settings ***
Documentation       STM32U575 SYSCFG (RM0456 Rev 7 section 15) and DCACHE1 (section 9) at register
...                 level, TZEN = 0, without firmware. The TrustZone rules of SYSCFG are checked by
...                 tests/cpu/stm32u575-trustzone.robot.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create Halted Machine

*** Variables ***
${SYSCFG}           0x46000400
${DCACHE}           0x40031400

*** Keywords ***
Create Halted Machine
    Create STM32U575 Machine
    Execute Command                 cpu IsHalted true

*** Test Cases ***
SYSCFG Should Have The Documented Reset Values
    # SYSCFG_FPUIMR 0x0000 001F, SYSCFG_CCCSR 0x0000 000A, SYSCFG_CCCR 0x0000 7878 (STM32U575).
    Word Should Be                  ${SYSCFG} + 0x08    0x0000001F
    Word Should Be                  ${SYSCFG} + 0x1C    0x0000000A
    Word Should Be                  ${SYSCFG} + 0x24    0x00007878
    Word Should Be                  ${SYSCFG} + 0x04    0
    Word Should Be                  ${SYSCFG} + 0x14    0

SYSCFG Should Make The Secure Registers RAZ/WI Without TrustZone
    # TZEN = 0: SYSCFG_SECCFGR and SYSCFG_CSLCKR are RAZ/WI.
    Write Word                      ${SYSCFG} + 0x00    0x0000000B
    Word Should Be                  ${SYSCFG} + 0x00    0
    Write Word                      ${SYSCFG} + 0x10    0x00000007
    Word Should Be                  ${SYSCFG} + 0x10    0

SYSCFG Should Keep The Lock Bits Until Reset
    # SYSCFG_CNSLCKR and SYSCFG_CFGR2 lock bits are set by software, cleared only by reset.
    Write Word                      ${SYSCFG} + 0x0C    0x00000003
    Write Word                      ${SYSCFG} + 0x0C    0
    Word Should Be                  ${SYSCFG} + 0x0C    0x00000003
    Write Word                      ${SYSCFG} + 0x14    0x0000000F
    Write Word                      ${SYSCFG} + 0x14    0
    Word Should Be                  ${SYSCFG} + 0x14    0x0000000F

SYSCFG Should Report A Compensation Cell Ready When Enabled
    # CCCSR: EN1 (bit 0) gives RDY1 (bit 8), EN2 (bit 2) RDY2 (bit 9).
    Write Word                      ${SYSCFG} + 0x1C    0x0000000B
    Word Should Be                  ${SYSCFG} + 0x1C    0x0000010B
    Write Word                      ${SYSCFG} + 0x1C    0x0000000F
    Word Should Be                  ${SYSCFG} + 0x1C    0x0000030F

DCACHE Should Have Finished The Reset Invalidation
    # DCACHE_CR 0; DCACHE_SR: the reset invalidation (BUSYF) has ended, BSYENDF = 1.
    Word Should Be                  ${DCACHE} + 0x00    0
    Word Should Be                  ${DCACHE} + 0x04    0x00000002

DCACHE Should Complete Invalidation And Range Commands At Once
    Write Word                      ${DCACHE} + 0x0C    0x00000002
    Word Should Be                  ${DCACHE} + 0x04    0
    # CACHEINV.
    Write Word                      ${DCACHE} + 0x00    0x00000002
    Word Should Be                  ${DCACHE} + 0x04    0x00000002
    # CACHECMD = clean and invalidate (3) with STARTCMD: CMDENDF (bit 4).
    Write Word                      ${DCACHE} + 0x28    0x20000000
    Write Word                      ${DCACHE} + 0x2C    0x20000FF0
    Write Word                      ${DCACHE} + 0x00    0x00000B01
    Bits Should Be Set              ${DCACHE} + 0x04    0x00000010
    Word Should Be                  ${DCACHE} + 0x28    0x20000000
    Write Word                      ${DCACHE} + 0x0C    0x00000010
    Bits Should Be Clear            ${DCACHE} + 0x04    0x00000010
