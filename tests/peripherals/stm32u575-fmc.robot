*** Settings ***
Documentation       STM32U575 FMC registers (RM0456 Rev 7 section 26) and the STM32U575I-EV SRAM on
...                 FMC bank 1 NE1, without firmware.
Resource            ${CURDIR}/../common/stm32u575.resource

*** Variables ***
${FMC}              0x420D0400
${EV_BOARD}         ${ROOT}/platforms/boards/stm32u575i_ev.repl
${SRAM}             0x60000000

*** Test Cases ***
Should Have The Documented Reset Values
    Create STM32U575 Machine
    # FMC_BCR1 0x0000 30DB, FMC_BCR2-4 0x0000 30D2, FMC_BTRx/BWTRx 0x0FFF FFFF, FMC_PCR
    # 0x0000 0018, FMC_SR 0x0000 0040, FMC_PMEM/PATT 0xFCFC FCFC.
    Word Should Be                  ${FMC} + 0x000     0x000030DB
    Word Should Be                  ${FMC} + 0x004     0x0FFFFFFF
    Word Should Be                  ${FMC} + 0x008     0x000030D2
    Word Should Be                  ${FMC} + 0x018     0x000030D2
    Word Should Be                  ${FMC} + 0x01C     0x0FFFFFFF
    Word Should Be                  ${FMC} + 0x104     0x0FFFFFFF
    Word Should Be                  ${FMC} + 0x11C     0x0FFFFFFF
    Word Should Be                  ${FMC} + 0x080     0x00000018
    Word Should Be                  ${FMC} + 0x084     0x00000040
    Word Should Be                  ${FMC} + 0x088     0xFCFCFCFC
    Word Should Be                  ${FMC} + 0x08C     0xFCFCFCFC
    Word Should Be                  ${FMC} + 0x094     0

Should Keep The Configuration And Report An Empty FIFO
    Create STM32U575 Machine
    Write Word                      ${FMC} + 0x000     0x800010DB
    Word Should Be                  ${FMC} + 0x000     0x800010DB
    Write Word                      ${FMC} + 0x004     0x00010203
    Word Should Be                  ${FMC} + 0x004     0x00010203
    # FEMPT (bit 6) is read-only and set.
    Write Word                      ${FMC} + 0x084     0
    Word Should Be                  ${FMC} + 0x084     0x00000040

Should Map The Evaluation Board SRAM On Bank 1
    Create STM32U575 Machine        ${EV_BOARD}
    Write Word                      ${SRAM}            0x12345678
    Write Word                      ${SRAM} + 0x1FFFFC    0x9ABCDEF0
    Word Should Be                  ${SRAM}            0x12345678
    Word Should Be                  ${SRAM} + 0x1FFFFC    0x9ABCDEF0
