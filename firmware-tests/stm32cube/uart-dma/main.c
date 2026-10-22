/* STM32CubeU5 HAL UART reception by GPDMA.
 *
 * Unmodified HAL. USART1 at 115200 baud from PCLK2 = 160 MHz, initialised as STM32CubeMX does;
 * GPDMA1 channel 2 serves the USART1_RX request, peripheral to memory, in normal mode. The
 * robot test writes "hello renode" to the terminal. Checks: HAL_UART_Receive_DMA of 12 bytes
 * ends with HAL_UART_RxCpltCallback and the buffer holds the text. */

#include <string.h>

#include "clock_160mhz.h"

UART_HandleTypeDef huart1;
DMA_HandleTypeDef hdmaRx;

volatile uint32_t step;
volatile uint32_t failedStep;
volatile uint32_t receiveComplete;
volatile uint32_t receiveError;
volatile uint32_t dataOk;

static uint8_t rxData[12];
static const char expected[] = "hello renode";
static const char ready[] = "STM32U575 HAL UART DMA ready\r\n";

void SysTick_Handler(void)
{
    HAL_IncTick();
}

void GPDMA1_Channel2_IRQHandler(void)
{
    HAL_DMA_IRQHandler(&hdmaRx);
}

void USART1_IRQHandler(void)
{
    HAL_UART_IRQHandler(&huart1);
}

void HAL_UART_RxCpltCallback(UART_HandleTypeDef *huart)
{
    if (huart == &huart1)
    {
        receiveComplete = 1u;
    }
}

void HAL_UART_ErrorCallback(UART_HandleTypeDef *huart)
{
    if (huart == &huart1)
    {
        receiveError = 1u;
    }
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

void HAL_UART_MspInit(UART_HandleTypeDef *huart)
{
    if (huart->Instance != USART1)
    {
        return;
    }
    GPIO_InitTypeDef init = {0};

    __HAL_RCC_USART1_CLK_ENABLE();
    __HAL_RCC_GPIOA_CLK_ENABLE();
    __HAL_RCC_GPDMA1_CLK_ENABLE();

    /* PA9 TX, PA10 RX, AF7 (STM32U575 datasheet alternate function table). */
    init.Pin = GPIO_PIN_9 | GPIO_PIN_10;
    init.Mode = GPIO_MODE_AF_PP;
    init.Pull = GPIO_NOPULL;
    init.Speed = GPIO_SPEED_FREQ_LOW;
    init.Alternate = GPIO_AF7_USART1;
    HAL_GPIO_Init(GPIOA, &init);

    hdmaRx.Instance = GPDMA1_Channel2;
    hdmaRx.Init.Request = GPDMA1_REQUEST_USART1_RX;
    hdmaRx.Init.BlkHWRequest = DMA_BREQ_SINGLE_BURST;
    hdmaRx.Init.Direction = DMA_PERIPH_TO_MEMORY;
    hdmaRx.Init.SrcInc = DMA_SINC_FIXED;
    hdmaRx.Init.DestInc = DMA_DINC_INCREMENTED;
    hdmaRx.Init.SrcDataWidth = DMA_SRC_DATAWIDTH_BYTE;
    hdmaRx.Init.DestDataWidth = DMA_DEST_DATAWIDTH_BYTE;
    hdmaRx.Init.Priority = DMA_LOW_PRIORITY_HIGH_WEIGHT;
    hdmaRx.Init.SrcBurstLength = 1;
    hdmaRx.Init.DestBurstLength = 1;
    hdmaRx.Init.TransferAllocatedPort = DMA_SRC_ALLOCATED_PORT0 | DMA_DEST_ALLOCATED_PORT1;
    hdmaRx.Init.TransferEventMode = DMA_TCEM_BLOCK_TRANSFER;
    hdmaRx.Init.Mode = DMA_NORMAL;
    if (HAL_DMA_Init(&hdmaRx) != HAL_OK)
    {
        failedStep = 100;
        for (;;)
        {
        }
    }
    __HAL_LINKDMA(huart, hdmarx, hdmaRx);

    HAL_NVIC_SetPriority(GPDMA1_Channel2_IRQn, 1, 0);
    HAL_NVIC_EnableIRQ(GPDMA1_Channel2_IRQn);
    HAL_NVIC_SetPriority(USART1_IRQn, 1, 0);
    HAL_NVIC_EnableIRQ(USART1_IRQn);
}

int main(void)
{
    check(HAL_Init(), 1);
    check(SystemClockConfig160MHz(), 2);

    huart1.Instance = USART1;
    huart1.Init.BaudRate = 115200;
    huart1.Init.WordLength = UART_WORDLENGTH_8B;
    huart1.Init.StopBits = UART_STOPBITS_1;
    huart1.Init.Parity = UART_PARITY_NONE;
    huart1.Init.Mode = UART_MODE_TX_RX;
    huart1.Init.HwFlowCtl = UART_HWCONTROL_NONE;
    huart1.Init.OverSampling = UART_OVERSAMPLING_16;
    huart1.Init.OneBitSampling = UART_ONE_BIT_SAMPLE_DISABLE;
    huart1.Init.ClockPrescaler = UART_PRESCALER_DIV1;
    huart1.AdvancedInit.AdvFeatureInit = UART_ADVFEATURE_NO_INIT;
    check(HAL_UART_Init(&huart1), 3);
    check(HAL_UARTEx_SetTxFifoThreshold(&huart1, UART_TXFIFO_THRESHOLD_1_8), 4);
    check(HAL_UARTEx_SetRxFifoThreshold(&huart1, UART_RXFIFO_THRESHOLD_1_8), 5);
    check(HAL_UARTEx_DisableFifoMode(&huart1), 6);

    check(HAL_UART_Receive_DMA(&huart1, rxData, sizeof(rxData)), 7);
    check(HAL_UART_Transmit(&huart1, (const uint8_t *)ready, strlen(ready), 100), 8);
    step = 9;
    while (!receiveComplete && !receiveError)
    {
    }
    dataOk = memcmp(rxData, expected, sizeof(rxData)) == 0;

    step = 10;
    for (;;)
    {
    }
}
