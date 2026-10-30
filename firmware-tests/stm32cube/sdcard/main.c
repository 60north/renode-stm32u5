/* STM32CubeU5 HAL SD card over SPI test.
 *
 * Unmodified HAL; an SD card in SPI mode is attached to SPI1 by the robot test and its chip
 * select to PD2, as on the MiniMate target pin map (SPI1 PB3/PB4/PB5, CS PD2). SPI1 is master,
 * full duplex, software NSS; the chip select is a GPIO driven by this firmware, and every byte is
 * a separate HAL_SPI_TransmitReceive, as in the common FatFs SPI drivers. Checks:
 * - initialization: CMD0, CMD8 (echo 0x1AA), ACMD41 until ready, CMD58 (OCR, CCS);
 * - CMD9: the CSD is version 2 (SDHC) and gives the sector count;
 * - CMD24: two sectors written with patterns; CMD17: both read back;
 * - a command interrupted by releasing CS is discarded: the next command is answered.
 * The card keeps its command state while CS stays low across HAL calls and drops it when CS
 * goes high. */

#include <string.h>

#include "clock_160mhz.h"

#define SECTOR_BYTES 512u
#define FIRST_SECTOR 100u

SPI_HandleTypeDef hspi1;

volatile uint32_t step;
volatile uint32_t failedStep;
volatile uint32_t highCapacity;
volatile uint32_t sectorCount;
volatile uint32_t sectorsOk;
volatile uint32_t interruptedCommandOk;

static uint8_t written[SECTOR_BYTES];
static uint8_t readBack[SECTOR_BYTES];

void SysTick_Handler(void)
{
    HAL_IncTick();
}

static void fail(uint32_t at)
{
    failedStep = at;
    for (;;)
    {
    }
}

static void check(int condition, uint32_t at)
{
    step = at;
    if (!condition)
    {
        fail(at);
    }
}

static uint8_t exchange(uint8_t value)
{
    uint8_t received = 0xFFu;
    if (HAL_SPI_TransmitReceive(&hspi1, &value, &received, 1u, 100u) != HAL_OK)
    {
        fail(90u);
    }
    return received;
}

static void select(void)
{
    HAL_GPIO_WritePin(GPIOD, GPIO_PIN_2, GPIO_PIN_RESET);
}

static void deselect(void)
{
    HAL_GPIO_WritePin(GPIOD, GPIO_PIN_2, GPIO_PIN_SET);
    (void)exchange(0xFFu);
}

/* Sends a command with CS low and returns its R1 response; CS stays low. */
static uint8_t command(uint8_t number, uint32_t argument, uint8_t crc)
{
    deselect();
    select();
    (void)exchange(0xFFu);
    (void)exchange((uint8_t)(0x40u | number));
    (void)exchange((uint8_t)(argument >> 24));
    (void)exchange((uint8_t)(argument >> 16));
    (void)exchange((uint8_t)(argument >> 8));
    (void)exchange((uint8_t)argument);
    (void)exchange(crc);
    for (uint32_t attempt = 0u; attempt < 10u; ++attempt)
    {
        const uint8_t response = exchange(0xFFu);
        if ((response & 0x80u) == 0u)
        {
            return response;
        }
    }
    return 0xFFu;
}

static int receiveData(uint8_t *data, uint32_t count)
{
    uint8_t token = 0xFFu;
    for (uint32_t attempt = 0u; attempt < 1000u && token == 0xFFu; ++attempt)
    {
        token = exchange(0xFFu);
    }
    if (token != 0xFEu)
    {
        return 0;
    }
    for (uint32_t index = 0u; index < count; ++index)
    {
        data[index] = exchange(0xFFu);
    }
    (void)exchange(0xFFu);
    (void)exchange(0xFFu);
    return 1;
}

static int waitReady(void)
{
    for (uint32_t attempt = 0u; attempt < 1000u; ++attempt)
    {
        if (exchange(0xFFu) == 0xFFu)
        {
            return 1;
        }
    }
    return 0;
}

static int writeSector(uint32_t address, const uint8_t *data)
{
    if (command(24u, address, 0x01u) != 0u)
    {
        return 0;
    }
    (void)exchange(0xFFu);
    (void)exchange(0xFEu);
    for (uint32_t index = 0u; index < SECTOR_BYTES; ++index)
    {
        (void)exchange(data[index]);
    }
    (void)exchange(0xFFu);
    (void)exchange(0xFFu);
    const int accepted = (exchange(0xFFu) & 0x1Fu) == 0x05u;
    const int ready = accepted && waitReady();
    deselect();
    return ready;
}

static int readSector(uint32_t address, uint8_t *data)
{
    const int success = command(17u, address, 0x01u) == 0u && receiveData(data, SECTOR_BYTES);
    deselect();
    return success;
}

void HAL_SPI_MspInit(SPI_HandleTypeDef *hspi)
{
    if (hspi->Instance == SPI1)
    {
        __HAL_RCC_SPI1_CLK_ENABLE();
        __HAL_RCC_GPIOB_CLK_ENABLE();
        GPIO_InitTypeDef gpio = {0};
        gpio.Pin = GPIO_PIN_3 | GPIO_PIN_4 | GPIO_PIN_5;
        gpio.Mode = GPIO_MODE_AF_PP;
        gpio.Pull = GPIO_NOPULL;
        gpio.Speed = GPIO_SPEED_FREQ_HIGH;
        gpio.Alternate = GPIO_AF5_SPI1;
        HAL_GPIO_Init(GPIOB, &gpio);
    }
}

static HAL_StatusTypeDef spiInit(void)
{
    hspi1.Instance = SPI1;
    hspi1.Init.Mode = SPI_MODE_MASTER;
    hspi1.Init.Direction = SPI_DIRECTION_2LINES;
    hspi1.Init.DataSize = SPI_DATASIZE_8BIT;
    hspi1.Init.CLKPolarity = SPI_POLARITY_LOW;
    hspi1.Init.CLKPhase = SPI_PHASE_1EDGE;
    hspi1.Init.NSS = SPI_NSS_SOFT;
    hspi1.Init.BaudRatePrescaler = SPI_BAUDRATEPRESCALER_256;
    hspi1.Init.FirstBit = SPI_FIRSTBIT_MSB;
    hspi1.Init.TIMode = SPI_TIMODE_DISABLE;
    hspi1.Init.CRCCalculation = SPI_CRCCALCULATION_DISABLE;
    hspi1.Init.CRCPolynomial = 0x7;
    hspi1.Init.NSSPMode = SPI_NSS_PULSE_DISABLE;
    hspi1.Init.NSSPolarity = SPI_NSS_POLARITY_LOW;
    hspi1.Init.FifoThreshold = SPI_FIFO_THRESHOLD_01DATA;
    hspi1.Init.MasterSSIdleness = SPI_MASTER_SS_IDLENESS_00CYCLE;
    hspi1.Init.MasterInterDataIdleness = SPI_MASTER_INTERDATA_IDLENESS_00CYCLE;
    hspi1.Init.MasterReceiverAutoSusp = SPI_MASTER_RX_AUTOSUSP_DISABLE;
    hspi1.Init.MasterKeepIOState = SPI_MASTER_KEEP_IO_STATE_ENABLE;
    hspi1.Init.IOSwap = SPI_IO_SWAP_DISABLE;
    hspi1.Init.ReadyMasterManagement = SPI_RDY_MASTER_MANAGEMENT_INTERNALLY;
    hspi1.Init.ReadyPolarity = SPI_RDY_POLARITY_HIGH;
    return HAL_SPI_Init(&hspi1);
}

static void initializeCard(void)
{
    for (uint32_t index = 0u; index < 10u; ++index)
    {
        (void)exchange(0xFFu);
    }
    check(command(0u, 0u, 0x95u) == 1u, 10u);
    check(command(8u, 0x1AAu, 0x87u) == 1u, 11u);
    uint8_t echo[4];
    for (uint32_t index = 0u; index < 4u; ++index)
    {
        echo[index] = exchange(0xFFu);
    }
    check(echo[2] == 0x01u && echo[3] == 0xAAu, 12u);
    uint8_t response = 0xFFu;
    for (uint32_t attempt = 0u; attempt < 100u && response != 0u; ++attempt)
    {
        (void)command(55u, 0u, 0x01u);
        response = command(41u, 0x40000000u, 0x01u);
    }
    check(response == 0u, 13u);
    check(command(58u, 0u, 0x01u) == 0u, 14u);
    const uint8_t ocr = exchange(0xFFu);
    for (uint32_t index = 0u; index < 3u; ++index)
    {
        (void)exchange(0xFFu);
    }
    deselect();
    highCapacity = (ocr & 0x40u) != 0u;
}

int main(void)
{
    check(HAL_Init() == HAL_OK, 1u);
    check(SystemClockConfig160MHz() == HAL_OK, 2u);
    __HAL_RCC_GPIOD_CLK_ENABLE();
    GPIO_InitTypeDef gpio = {0};
    gpio.Pin = GPIO_PIN_2;
    gpio.Mode = GPIO_MODE_OUTPUT_PP;
    gpio.Pull = GPIO_NOPULL;
    gpio.Speed = GPIO_SPEED_FREQ_HIGH;
    HAL_GPIO_WritePin(GPIOD, GPIO_PIN_2, GPIO_PIN_SET);
    HAL_GPIO_Init(GPIOD, &gpio);
    check(spiInit() == HAL_OK, 3u);

    initializeCard();
    check(highCapacity, 15u);

    uint8_t csd[16];
    const int csdRead = command(9u, 0u, 0x01u) == 0u && receiveData(csd, sizeof(csd));
    deselect();
    check(csdRead && (csd[0] >> 6) == 1u, 20u);
    const uint32_t size = ((uint32_t)(csd[7] & 0x3Fu) << 16) | ((uint32_t)csd[8] << 8) | csd[9];
    sectorCount = (size + 1u) * 1024u;

    for (uint32_t sector = 0u; sector < 2u; ++sector)
    {
        for (uint32_t index = 0u; index < SECTOR_BYTES; ++index)
        {
            written[index] = (uint8_t)(index * 7u + sector * 0x55u + 1u);
        }
        check(writeSector(FIRST_SECTOR + sector, written), 30u + sector);
        memset(readBack, 0, sizeof(readBack));
        check(readSector(FIRST_SECTOR + sector, readBack), 40u + sector);
        check(memcmp(written, readBack, SECTOR_BYTES) == 0, 50u + sector);
        sectorsOk += 1u;
    }

    /* Two bytes of CMD17, then CS high: the card must drop the partial command. */
    select();
    (void)exchange(0x40u | 17u);
    (void)exchange(0x00u);
    deselect();
    check(readSector(FIRST_SECTOR, readBack), 60u);
    for (uint32_t index = 0u; index < SECTOR_BYTES; ++index)
    {
        written[index] = (uint8_t)(index * 7u + 1u);
    }
    check(memcmp(written, readBack, SECTOR_BYTES) == 0, 61u);
    interruptedCommandOk = 1u;

    step = 99u;
    for (;;)
    {
    }
}
