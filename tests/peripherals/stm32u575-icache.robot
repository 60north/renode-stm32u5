*** Settings ***
Documentation       STM32U575 ICACHE maintenance operations (RM0456 Rev 7 section 8).
...                 The cache contents are not simulated; these tests cover the register
...                 interface used by STM32CubeU5 (HAL_ICACHE_Enable/Disable/Invalidate).
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine

*** Variables ***
${ICACHE_CR}        0x40030400
${ICACHE_SR}        0x40030404
${ICACHE_IER}       0x40030408
${ICACHE_FCR}       0x4003040C

${CR_EN}            0x00000001
${CR_CACHEINV}      0x00000002
${CR_WAYSEL}        0x00000004
${SR_BUSYF}         0x00000001
${SR_BSYENDF}       0x00000002
${BSYENDIE}         0x00000002
${CBSYENDF}         0x00000002

*** Test Cases ***
Should Have Finished The Reset Invalidation
    [Documentation]                 Documented deviation: RM0456 gives ICACHE_SR = BUSYF after reset.
    Word Should Be                  ${ICACHE_SR}       ${SR_BSYENDF}

Should Complete An Explicit Invalidation
    Write Word                      ${ICACHE_FCR}      ${CBSYENDF}
    Word Should Be                  ${ICACHE_SR}       0
    Write Word                      ${ICACHE_CR}       ${CR_WAYSEL} | ${CR_CACHEINV}
    Word Should Be                  ${ICACHE_SR}       ${SR_BSYENDF}
    # CACHEINV is cleared by hardware.
    Word Should Be                  ${ICACHE_CR}       ${CR_WAYSEL}

Should Invalidate When The Cache Is Disabled
    Write Word                      ${ICACHE_CR}       ${CR_WAYSEL} | ${CR_EN}
    Write Word                      ${ICACHE_FCR}      ${CBSYENDF}
    Write Word                      ${ICACHE_CR}       ${CR_WAYSEL}
    Bits Should Be Clear            ${ICACHE_CR}       ${CR_EN}
    Word Should Be                  ${ICACHE_SR}       ${SR_BSYENDF}

Should Change Associativity Only While Disabled
    Write Word                      ${ICACHE_CR}       ${CR_EN}
    Word Should Be                  ${ICACHE_CR}       ${CR_EN}
    Write Word                      ${ICACHE_CR}       ${CR_EN} | ${CR_WAYSEL}
    Word Should Be                  ${ICACHE_CR}       ${CR_EN}
    Write Word                      ${ICACHE_CR}       0
    Write Word                      ${ICACHE_CR}       ${CR_WAYSEL}
    Word Should Be                  ${ICACHE_CR}       ${CR_WAYSEL}

Should Signal Busy End Through The Interrupt
    Interrupt Line Should Be        icache             False
    Write Word                      ${ICACHE_IER}      ${BSYENDIE}
    Interrupt Line Should Be        icache             True
    Write Word                      ${ICACHE_FCR}      ${CBSYENDF}
    Interrupt Line Should Be        icache             False
    Write Word                      ${ICACHE_CR}       ${CR_WAYSEL} | ${CR_CACHEINV}
    Interrupt Line Should Be        icache             True
