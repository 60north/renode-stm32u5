/* STM32CubeU5 HAL flash programming test.
 *
 * Unmodified HAL. The last page of bank 2 (page 127, 0x081FE000) is erased with
 * HAL_FLASHEx_Erase and programmed with HAL_FLASH_Program (quad-words, 128 bits), as firmware
 * storing settings does. Programming a quad-word that is not erased fails with PROGERR, except
 * when all zeros are written (RM0456 section 7.3.9). After a reset the firmware finds the marker
 * written in the first run and does not erase the page again. The robot test reads the global
 * variables. */

#include "clock_160mhz.h"

#define TEST_PAGE_ADDRESS 0x081FE000UL
#define MARKER 0x4D4D4D31UL

volatile uint32_t step;
volatile uint32_t failedStep;
volatile uint32_t foundAfterReset;
volatile uint32_t erasedWord;
volatile uint32_t programmedWords[4];
volatile uint32_t overwriteStatus;
volatile uint32_t overwriteError;
volatile uint32_t zeroStatus;
volatile uint32_t zeroWord;

static const uint32_t firstData[4] = {MARKER, 0x11111111UL, 0x22222222UL, 0x33333333UL};
static const uint32_t secondData[4] = {0x0F0F0F0FUL, 0x0F0F0F0FUL, 0x0F0F0F0FUL, 0x0F0F0F0FUL};
static const uint32_t zeroData[4] = {0, 0, 0, 0};

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

static volatile uint32_t *word(uint32_t address)
{
    return (volatile uint32_t *)address;
}

int main(void)
{
    check(HAL_Init(), 1);
    check(SystemClockConfig160MHz(), 2);

    if (*word(TEST_PAGE_ADDRESS) == MARKER)
    {
        foundAfterReset = 1;
        step = 10;
        for (;;)
        {
        }
    }

    check(HAL_FLASH_Unlock(), 3);

    FLASH_EraseInitTypeDef erase = {0};
    uint32_t pageError = 0;
    erase.TypeErase = FLASH_TYPEERASE_PAGES;
    erase.Banks = FLASH_BANK_2;
    erase.Page = 127;
    erase.NbPages = 1;
    check(HAL_FLASHEx_Erase(&erase, &pageError), 4);
    erasedWord = *word(TEST_PAGE_ADDRESS + 16);

    /* Second quad-word: programmed, then programmed again without an erase (PROGERR), then
     * programmed with all zeros (allowed). */
    check(HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, TEST_PAGE_ADDRESS + 16, (uint32_t)secondData), 5);
    overwriteStatus = HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, TEST_PAGE_ADDRESS + 16, (uint32_t)firstData);
    overwriteError = HAL_FLASH_GetError();
    zeroStatus = HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, TEST_PAGE_ADDRESS + 16, (uint32_t)zeroData);
    zeroWord = *word(TEST_PAGE_ADDRESS + 16);

    /* First quad-word with the marker, read back. */
    check(HAL_FLASH_Program(FLASH_TYPEPROGRAM_QUADWORD, TEST_PAGE_ADDRESS, (uint32_t)firstData), 6);
    for (uint32_t i = 0; i < 4; i++)
    {
        programmedWords[i] = *word(TEST_PAGE_ADDRESS + 4 * i);
    }
    check(HAL_FLASH_Lock(), 7);

    step = 9;
    for (;;)
    {
    }
}
