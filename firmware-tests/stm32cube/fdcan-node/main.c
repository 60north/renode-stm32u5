/* STM32CubeU5 HAL FDCAN node on a Renode CAN hub.
 *
 * Unmodified HAL. FDCAN1 in normal mode at 250 kbit/s (classic CAN, the NMEA 2000 bit rate) from
 * the 16 MHz HSE: prescaler 4, 16 time quanta per bit (1 + 13 + 2). Built twice:
 * - NODE_TX: sends FRAME_COUNT frames with 29-bit identifiers through the Tx FIFO, waiting for a
 *   free element when the FIFO is full; the free level right after the first three requests is
 *   stored (0 while the frames are still being transmitted).
 * - NODE_RX: accepts all extended frames in Rx FIFO 0 (blocking mode), waits RX_READ_DELAY_MS
 *   (0 by default) and then reads the FIFO; the identifiers, the first data byte and the message
 *   lost flag are stored. */

#include "clock_160mhz.h"

#define FRAME_COUNT 6
#define FIRST_ID 0x18EEFF00UL
#ifndef RX_READ_DELAY_MS
#define RX_READ_DELAY_MS 0
#endif

FDCAN_HandleTypeDef hfdcan1;

volatile uint32_t step;
volatile uint32_t failedStep;
volatile uint32_t sentCount;
volatile uint32_t freeLevelAfterBurst;
volatile uint32_t receivedCount;
volatile uint32_t receivedIds[FRAME_COUNT];
volatile uint32_t receivedFirstByte[FRAME_COUNT];
volatile uint32_t messageLost;

void SysTick_Handler(void)
{
    HAL_IncTick();
}

static void check(HAL_StatusTypeDef status, uint32_t at)
{
    step = at;
    if (status != HAL_OK)
    {
        failedStep = at;
        for (;;)
        {
        }
    }
}

void HAL_FDCAN_MspInit(FDCAN_HandleTypeDef *hfdcan)
{
    RCC_PeriphCLKInitTypeDef clock = {0};

    if (hfdcan->Instance != FDCAN1)
    {
        return;
    }
    clock.PeriphClockSelection = RCC_PERIPHCLK_FDCAN1;
    clock.Fdcan1ClockSelection = RCC_FDCAN1CLKSOURCE_HSE;
    check(HAL_RCCEx_PeriphCLKConfig(&clock), 100);
    __HAL_RCC_FDCAN1_CLK_ENABLE();
}

static void hseConfig(void)
{
    RCC_OscInitTypeDef oscillator = {0};

    oscillator.OscillatorType = RCC_OSCILLATORTYPE_HSE;
    oscillator.HSEState = RCC_HSE_ON;
    oscillator.PLL.PLLState = RCC_PLL_NONE;
    check(HAL_RCC_OscConfig(&oscillator), 3);
}

static void fdcanConfig(void)
{
    hfdcan1.Instance = FDCAN1;
    hfdcan1.Init.ClockDivider = FDCAN_CLOCK_DIV1;
    hfdcan1.Init.FrameFormat = FDCAN_FRAME_CLASSIC;
    hfdcan1.Init.Mode = FDCAN_MODE_NORMAL;
    hfdcan1.Init.AutoRetransmission = ENABLE;
    hfdcan1.Init.TransmitPause = DISABLE;
    hfdcan1.Init.ProtocolException = DISABLE;
    hfdcan1.Init.NominalPrescaler = 4;
    hfdcan1.Init.NominalSyncJumpWidth = 1;
    hfdcan1.Init.NominalTimeSeg1 = 13;
    hfdcan1.Init.NominalTimeSeg2 = 2;
    hfdcan1.Init.DataPrescaler = 1;
    hfdcan1.Init.DataSyncJumpWidth = 1;
    hfdcan1.Init.DataTimeSeg1 = 1;
    hfdcan1.Init.DataTimeSeg2 = 1;
    hfdcan1.Init.StdFiltersNbr = 0;
    hfdcan1.Init.ExtFiltersNbr = 0;
    hfdcan1.Init.TxFifoQueueMode = FDCAN_TX_FIFO_OPERATION;
    check(HAL_FDCAN_Init(&hfdcan1), 4);
    check(HAL_FDCAN_ConfigGlobalFilter(&hfdcan1, FDCAN_REJECT, FDCAN_ACCEPT_IN_RX_FIFO0,
                                       FDCAN_REJECT_REMOTE, FDCAN_REJECT_REMOTE), 5);
    check(HAL_FDCAN_Start(&hfdcan1), 6);
}

#if defined(NODE_TX)
static void run(void)
{
    FDCAN_TxHeaderTypeDef header = {0};
    uint8_t data[8] = {0};

    header.IdType = FDCAN_EXTENDED_ID;
    header.TxFrameType = FDCAN_DATA_FRAME;
    header.DataLength = FDCAN_DLC_BYTES_8;
    header.ErrorStateIndicator = FDCAN_ESI_ACTIVE;
    header.BitRateSwitch = FDCAN_BRS_OFF;
    header.FDFormat = FDCAN_CLASSIC_CAN;
    header.TxEventFifoControl = FDCAN_NO_TX_EVENTS;
    header.MessageMarker = 0;

    for (uint32_t i = 0; i < FRAME_COUNT; i++)
    {
        while (HAL_FDCAN_GetTxFifoFreeLevel(&hfdcan1) == 0)
        {
        }
        header.Identifier = FIRST_ID + i;
        data[0] = (uint8_t)(0xA0 + i);
        check(HAL_FDCAN_AddMessageToTxFifoQ(&hfdcan1, &header, data), 7);
        sentCount++;
        if (i == 2)
        {
            freeLevelAfterBurst = HAL_FDCAN_GetTxFifoFreeLevel(&hfdcan1);
        }
    }
}
#elif defined(NODE_RX)
static void run(void)
{
    FDCAN_RxHeaderTypeDef header;
    uint8_t data[8];

    HAL_Delay(RX_READ_DELAY_MS);
    for (;;)
    {
        if (__HAL_FDCAN_GET_FLAG(&hfdcan1, FDCAN_FLAG_RX_FIFO0_MESSAGE_LOST))
        {
            messageLost = 1;
        }
        if (HAL_FDCAN_GetRxFifoFillLevel(&hfdcan1, FDCAN_RX_FIFO0) == 0)
        {
            continue;
        }
        check(HAL_FDCAN_GetRxMessage(&hfdcan1, FDCAN_RX_FIFO0, &header, data), 8);
        if (receivedCount < FRAME_COUNT)
        {
            receivedIds[receivedCount] = header.Identifier;
            receivedFirstByte[receivedCount] = data[0];
        }
        receivedCount++;
    }
}
#else
#error "Define NODE_TX or NODE_RX"
#endif

int main(void)
{
    check(HAL_Init(), 1);
    check(SystemClockConfig160MHz(), 2);
    hseConfig();
    fdcanConfig();
    run();

    step = 9;
    for (;;)
    {
    }
}
