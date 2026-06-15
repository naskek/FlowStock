# FlowStock AI invariants

## Stock / Ledger
- Остатки считаются только из ledger.
- Закрытые документы не редактируются.
- Исправления делаются отдельными документами: корректировка, сторно или новый документ-движение.
- UI не является источником истины для остатков, резервов, производственных потребностей и маркировки.

## Server as source of truth
- WPF/Web/TSD не должны доверять client-calculated production need / reservation / marking quantities.
- Server/API должен пересчитывать критичные количества из DB внутри команды и транзакции.
- Повторные вызовы критичных операций должны быть идемпотентны там, где это возможно.

## CUSTOMER orders
- CUSTOMER full PRODUCTION_RECEIPT -> status Готов / ACCEPTED.
- CUSTOMER full OUTBOUND -> status Выполнен / SHIPPED.
- CUSTOMER order может использовать warehouse HU и planned production pallets одновременно.
- Planned production pallets должны покрывать только shortage, а не весь заказ, если часть уже есть на складе.

## INTERNAL orders
- INTERNAL не должен попадать в OUT picker.
- INTERNAL не требует OUTBOUND.
- INTERNAL full PRODUCTION_RECEIPT -> status Выполнен / SHIPPED.
- DRAFT/Черновик означает реальный редактируемый черновик, а не просто отсутствие выпуска.

## Palletized production
- Если PRD имеет production_pallets, legacy HU assignment / auto-distribute / capacity-distribute должны быть заблокированы.
- Для PRD с production_pallets close/finalize не должен повторно писать ledger.
- FILLED production_pallet уже является фактом выпуска.
- Mixed pallet должен писать ledger по компонентам, а не одной агрегированной строкой.

## Marking / Честный Знак
- Основной workflow маркировки order-based: POST /api/orders/{orderId}/marking/export.
- CUSTOMER marking export должен создавать коды только на реальную planned production / shortage.
- Складские HU с уже существующими кодами не должны требовать новых кодов.
- Marking export должен быть идемпотентным.
- Старый /api/marking/create-from-production-needs считается legacy/journal-compatible, не основной путь.

## TSD
- TSD scan/fill planned HU is the committing fact for production filling.
- Unknown/ad-hoc HU не принимаются в нормальном filling flow.
- Operator workflow должен быть простым: order -> scan planned HU -> confirm.
- OUT scan должен использовать warehouse-bound HU и FILLED production_pallet HU.
