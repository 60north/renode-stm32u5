*** Settings ***
Documentation       STM32U5 FDCAN register behaviour without firmware (RM0456 Rev 7 section 70):
...                 FDCAN_TEST write access controlled by CCCR.TEST (70.3.4, "Test modes"; 70.4.4, 70.4.6).
...                 Reset values are in stm32u575-reset-values.robot; loop-back transfers are
...                 covered by the ST example FDCAN_Loopback.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine

*** Variables ***
${FDCAN1}           0x4000A400
${TEST}             0x10
${CCCR}             0x18
${LBCK}             ${{1 << 4}}
${INIT}             ${{1 << 0}}
${CCE}              ${{1 << 1}}
${TEST_ENABLE}      ${{1 << 7}}

*** Test Cases ***
Should Allow Loop-Back Mode Only In Test Mode
    Write Word                      ${FDCAN1} + ${CCCR}    ${INIT} | ${CCE}
    Write Word                      ${FDCAN1} + ${TEST}    ${LBCK}
    Word Should Be                  ${FDCAN1} + ${TEST}    0
    Write Word                      ${FDCAN1} + ${CCCR}    ${INIT} | ${CCE} | ${TEST_ENABLE}
    Write Word                      ${FDCAN1} + ${TEST}    ${LBCK}
    Word Should Be                  ${FDCAN1} + ${TEST}    ${LBCK}
    # FDCAN_TEST holds reset values when test mode is disabled.
    Write Word                      ${FDCAN1} + ${CCCR}    ${INIT} | ${CCE}
    Word Should Be                  ${FDCAN1} + ${TEST}    0
