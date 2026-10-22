*** Settings ***
Documentation       STM32U5 GPDMA register behaviour without firmware (RM0456 Rev 7 section 17):
...                 memory-to-memory block transfer with BNDT counted in bytes (17.8.12), transfer
...                 complete flag and interrupt, on the first and last channels of both interrupt
...                 ranges. Linked-list mode and 2D addressing are not modelled.
Resource            ${CURDIR}/../common/stm32u575.resource
Test Setup          Create STM32U575 Machine

*** Variables ***
${GPDMA1}           0x40020000
# Channel x registers at 0x80 * x plus (17.8.7 - 17.8.15):
${CxFCR}            0x5C
${CxSR}             0x60
${CxCR}             0x64
${CxTR1}            0x90
${CxTR2}            0x94
${CxBR1}            0x98
${CxSAR}            0x9C
${CxDAR}            0xA0
${SOURCE}           0x20001000
${DESTINATION}      0x20002000
${EN}               ${{1 << 0}}
${TCIE}             ${{1 << 8}}
${TCF}              ${{1 << 8}}
${SWREQ}            ${{1 << 9}}
# CxTR1: SDW_LOG2 = DDW_LOG2 = 2 (32-bit), SINC, DINC.
${WORDS_INCREMENTED}    ${{(2 << 0) | (1 << 3) | (2 << 16) | (1 << 19)}}
${NVIC_ISPR0}       0xE000E200

*** Keywords ***
Channel Register
    [Arguments]                     ${channel}    ${register}
    ${address}=                     Evaluate           ${GPDMA1} + 0x80 * ${channel} + ${register}
    RETURN                          ${address}

Fill Memory
    Write Word                      ${SOURCE}          0x11111111
    Write Word                      ${SOURCE} + 4      0x22222222
    Write Word                      ${SOURCE} + 8      0x33333333
    Write Word                      ${DESTINATION} + 8    0xAAAAAAAA

Start Memory To Memory Transfer
    [Arguments]                     ${channel}    ${bytes}
    ${tr1}=                         Channel Register    ${channel}    ${CxTR1}
    ${tr2}=                         Channel Register    ${channel}    ${CxTR2}
    ${br1}=                         Channel Register    ${channel}    ${CxBR1}
    ${sar}=                         Channel Register    ${channel}    ${CxSAR}
    ${dar}=                         Channel Register    ${channel}    ${CxDAR}
    ${cr}=                          Channel Register    ${channel}    ${CxCR}
    Write Word                      ${tr1}             ${WORDS_INCREMENTED}
    Write Word                      ${tr2}             ${SWREQ}
    Write Word                      ${br1}             ${bytes}
    Write Word                      ${sar}             ${SOURCE}
    Write Word                      ${dar}             ${DESTINATION}
    Write Word                      ${cr}              ${TCIE} | ${EN}

Channel Should Copy Exactly BNDT Bytes
    [Arguments]                     ${channel}
    Fill Memory
    Start Memory To Memory Transfer    ${channel}    8
    Word Should Be                  ${DESTINATION}        0x11111111
    Word Should Be                  ${DESTINATION} + 4    0x22222222
    # The word after the block is not written.
    Word Should Be                  ${DESTINATION} + 8    0xAAAAAAAA
    ${br1}=                         Channel Register    ${channel}    ${CxBR1}
    Field Should Be                 ${br1}             0    16    0

Channel Should Signal Transfer Complete
    [Arguments]                     ${channel}    ${irq}
    Fill Memory
    Start Memory To Memory Transfer    ${channel}    8
    ${sr}=                          Channel Register    ${channel}    ${CxSR}
    ${fcr}=                         Channel Register    ${channel}    ${CxFCR}
    Bits Should Be Set              ${sr}              ${TCF}
    # Pending bit of the channel interrupt in NVIC_ISPRn.
    ${ispr}=                        Evaluate           ${NVIC_ISPR0} + 4 * (${irq} // 32)
    Bits Should Be Set              ${ispr}            1 << (${irq} % 32)
    Write Word                      ${fcr}             ${TCF}
    Bits Should Be Clear            ${sr}              ${TCF}

*** Test Cases ***
Should Copy Exactly BNDT Bytes On Channel 0
    Channel Should Copy Exactly BNDT Bytes    0

Should Copy Exactly BNDT Bytes On Channel 15
    Channel Should Copy Exactly BNDT Bytes    15

# GPDMA1_Channel0_IRQn = 29, GPDMA1_Channel8_IRQn = 80, GPDMA1_Channel15_IRQn = 87 (stm32u575xx.h).
Should Signal Transfer Complete On Channel 0
    Channel Should Signal Transfer Complete    0    29

Should Signal Transfer Complete On Channel 8
    Channel Should Signal Transfer Complete    8    80

Should Signal Transfer Complete On Channel 15
    Channel Should Signal Transfer Complete    15    87

Should Move I2C1 Received Bytes On Its Request
    # Channel 3: peripheral to memory, REQSEL = i2c1_rx_dma (12, RM0456 17.3.3), I2C1_RXDR
    # (offset 0x24) to memory; I2C1 reads 3 bytes from a target with RXDMAEN set.
    Execute Command                 machine LoadPlatformDescriptionFromString "target: Mocks.DummyI2CSlave @ i2c1 0x42"
    Execute Command                 sysbus.i2c1.target EnqueueResponseByte 0x5A
    Execute Command                 sysbus.i2c1.target EnqueueResponseByte 0x6B
    Execute Command                 sysbus.i2c1.target EnqueueResponseByte 0x7C
    Write Word                      ${DESTINATION}     0xAAAAAAAA
    ${tr1}=                         Channel Register    3    ${CxTR1}
    ${tr2}=                         Channel Register    3    ${CxTR2}
    ${br1}=                         Channel Register    3    ${CxBR1}
    ${sar}=                         Channel Register    3    ${CxSAR}
    ${dar}=                         Channel Register    3    ${CxDAR}
    ${cr}=                          Channel Register    3    ${CxCR}
    # CxTR1: 8-bit data, DINC.
    Write Word                      ${tr1}             1 << 19
    Write Word                      ${tr2}             12
    Write Word                      ${br1}             3
    Write Word                      ${sar}             0x40005424
    Write Word                      ${dar}             ${DESTINATION}
    Write Word                      ${cr}              ${EN}
    # The channel waits for requests.
    Bits Should Be Set              ${cr}              ${EN}
    # I2C1_CR1: PE, RXDMAEN; I2C1_CR2: SADD = 0x42 << 1, RD_WRN, NBYTES = 3, AUTOEND, START.
    Write Word                      0x40005400         (1 << 0) | (1 << 15)
    Write Word                      0x40005404         (0x42 << 1) | (1 << 10) | (3 << 16) | (1 << 25) | (1 << 13)
    Word Should Be                  ${DESTINATION}     0xAA7C6B5A
    Field Should Be                 ${br1}             0    16    0
    Bits Should Be Clear            ${cr}              ${EN}
