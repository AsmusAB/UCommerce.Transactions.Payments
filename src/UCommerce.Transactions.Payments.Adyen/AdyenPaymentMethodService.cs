﻿using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Web;
using Adyen.Model.Checkout;
using Adyen.Model.Modification;
using Adyen.Model.Notification;
using Adyen.Notification;
using Adyen.Util;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Ucommerce.EntitiesV2;
using Ucommerce.Infrastructure.Logging;
using Ucommerce.Transactions.Payments.Adyen.Extensions;
using Ucommerce.Transactions.Payments.Adyen.Factories;
using Ucommerce.Web;

namespace Ucommerce.Transactions.Payments.Adyen
{
    public class AdyenPaymentMethodService : ExternalPaymentMethodService
    {
        private const string PaymentReferenceKey = "merchantReference";

        private readonly IAdyenClientFactory _clientFactory;
        private readonly IRepository<Payment> _paymentRepository;
        private readonly IAbsoluteUrlService _absoluteUrlService;
        private readonly ILoggingService _loggingService;
        private string webHookContent;

        public AdyenPaymentMethodService(ILoggingService loggingService,
            IAdyenClientFactory clientFactory,
            IRepository<Payment> paymentRepository, IAbsoluteUrlService absoluteUrlService)
        {
            _loggingService = loggingService;
            _clientFactory = clientFactory ?? throw new ArgumentNullException(nameof(clientFactory));
            _paymentRepository = paymentRepository;
            _absoluteUrlService = absoluteUrlService;
        }

        /// <summary>
        /// Extracts payment from request using the default payment gateway callback extractor.
        /// </summary>
        /// <param name="httpRequest"></param>
        /// <returns></returns>
        public override Payment Extract(HttpRequest httpRequest)
        {
            var contentJson = ReadWebHookContent(httpRequest);
            var jsonObj = (JObject?)JsonConvert.DeserializeObject(contentJson);
            var reference = jsonObj?["notificationItems"]?[0]?["NotificationRequestItem"]?[PaymentReferenceKey]
                ?.Value<string>();

            return _paymentRepository.Select(x => x.ReferenceId == reference).FirstOrDefault() ??
                   throw new NullReferenceException(
                       $"Could not find a payment with ReferenceId: '{reference}'.");
        }

        public override void ProcessCallback(Payment payment)
        {
            string hmacKey = payment.PaymentMethod.DynamicProperty<string>()?
                .HmacKey ?? string.Empty;

            var hmacValidator = new HmacValidator();
            var notificationHandler = new NotificationHandler();
            var contentJson = ReadWebHookContent(HttpContext.Current.Request);
            var handleNotificationRequest = notificationHandler.HandleNotificationRequest(contentJson);

            IList<NotificationRequestItemContainer> notificationRequestItemContainers =
                handleNotificationRequest.NotificationItemContainers;
            foreach (var notificationRequestItemContainer in notificationRequestItemContainers)
            {
                var notificationItem = notificationRequestItemContainer.NotificationItem;
                // Handle the notification
                if (hmacValidator.IsValidHmac(notificationItem, hmacKey))
                {
                    // Process the notification based on the eventCode
                    string eventCode = notificationItem.EventCode;

                    // This notification is for a payment.
                    if (notificationItem.Success)
                    {
                        var newPaymentStatus = eventCode == "AUTHORISATION"
                            ? PaymentStatus.Get((int)PaymentStatusCode.Authorized)
                            : eventCode == "CAPTURE"
                                ? PaymentStatus.Get((int)PaymentStatusCode.Acquired)
                                : null;

                        if (newPaymentStatus != null)
                        {
                            payment.PaymentStatus = newPaymentStatus;
                            payment.TransactionId = notificationItem.PspReference;
                            ProcessPaymentRequest(new PaymentRequest(payment.PurchaseOrder, payment));
                        }
                    }
                    else
                    {
                        payment.PaymentStatus = PaymentStatus.Get((int)PaymentStatusCode.Declined);
                    }

                    payment.Save();
                    return;
                }

                _loggingService.Information<AdyenPaymentMethodService>(
                    $"Failed verifying HMAC key for {notificationItem.PspReference}.");
            }
        }

        public override string RenderPage(PaymentRequest paymentRequest)
        {
            throw new NotSupportedException("Adyen does not need a local form. Use RequestPayment instead.");
        }

        public override Payment RequestPayment(PaymentRequest paymentRequest)
        {
            if (paymentRequest.Payment == null)
            {
                paymentRequest.Payment = CreatePayment(paymentRequest);
            }

            var metadata = new Dictionary<string, string>
            {
                { "orderReference", paymentRequest.Payment.ReferenceId },
                { "orderId", paymentRequest.PurchaseOrder.Guid.ToString("D") },
                { "orderNumber", paymentRequest.PurchaseOrder.OrderNumber }
            };


            string merchantAccount = paymentRequest.PaymentMethod.DynamicProperty<string>()
                ?
                .MerchantAccount ?? string.Empty;
            string returnUrl = _absoluteUrlService.GetAbsoluteUrl(paymentRequest.PaymentMethod.DynamicProperty<string>()
                ?
                .ReturnUrl) ?? string.Empty;

            // Create a payment request
            var amount = new Amount(paymentRequest.PurchaseOrder.BillingCurrency.ISOCode,
                Convert.ToInt64(paymentRequest.Amount.Value * 100));

            var adyenPaymentRequest = new CreatePaymentLinkRequest(amount: amount, merchantAccount: merchantAccount,
                reference: paymentRequest.Payment.ReferenceId)
            {
                ReturnUrl = returnUrl,
                ShopperEmail = paymentRequest.PurchaseOrder.Customer?.EmailAddress,
                ShopperReference = paymentRequest.PurchaseOrder.Customer?.Guid.ToString(),
                ShopperName = new Name(paymentRequest.PurchaseOrder.BillingAddress?.FirstName,
                    paymentRequest.PurchaseOrder.BillingAddress?.LastName),
                CountryCode = paymentRequest.PurchaseOrder.BillingAddress?.Country.Culture.Split('-')
                    .Last(),
                Metadata = metadata
            };

            var checkout = _clientFactory.GetCheckout(paymentRequest.PaymentMethod);

            var result = checkout.PaymentLinks(adyenPaymentRequest);

            if (string.IsNullOrWhiteSpace(result.Url))
            {
                throw new InvalidOperationException("Could not redirect to Adyen payment page.");
            }

            HttpContext.Current.Response.Redirect(result.Url);

            return paymentRequest.Payment;
        }

        protected override bool AcquirePaymentInternal(Payment payment, out string status)
        {
            var amount = new global::Adyen.Model.Amount(payment.PurchaseOrder.BillingCurrency.ISOCode,
                Convert.ToInt64(payment.Amount * 100));
            string merchantAccount = payment.PaymentMethod.DynamicProperty<string>()
                ?
                .MerchantAccount ?? string.Empty;

            var modification = _clientFactory.GetModification(payment.PaymentMethod);
            var result = modification.Capture(new CaptureRequest
            {
                MerchantAccount = merchantAccount,
                ModificationAmount = amount,
                OriginalReference = payment.TransactionId
            });

            status = result.Status;

            if (result.Response == global::Adyen.Model.Enum.ResponseEnum.CaptureReceived)
            {
                return true;
            }

            return false;
        }

        protected override bool RefundPaymentInternal(Payment payment, out string status)
        {
            var amount = new global::Adyen.Model.Amount(payment.PurchaseOrder.BillingCurrency.ISOCode,
                Convert.ToInt64(payment.Amount * 100));
            string merchantAccount = payment.PaymentMethod.DynamicProperty<string>()
                ?
                .MerchantAccount ?? string.Empty;

            var modification = _clientFactory.GetModification(payment.PaymentMethod);
            var result = modification.Refund(new RefundRequest
            {
                MerchantAccount = merchantAccount,
                ModificationAmount = amount,
                OriginalReference = payment.TransactionId
            });

            status = result.Status;

            if (result.Response == global::Adyen.Model.Enum.ResponseEnum.RefundReceived ||
                result.Response == global::Adyen.Model.Enum.ResponseEnum.CancelOrRefundReceived)
            {
                return true;
            }

            return false;
        }

        protected override bool CancelPaymentInternal(Payment payment, out string status)
        {
            string merchantAccount = payment.PaymentMethod.DynamicProperty<string>()
                ?
                .MerchantAccount ?? string.Empty;

            var modification = _clientFactory.GetModification(payment.PaymentMethod);

            var result = modification.Cancel(new CancelRequest
            {
                MerchantAccount = merchantAccount,
                OriginalReference = payment.TransactionId
            });

            status = result.Status;

            if (result.Response == global::Adyen.Model.Enum.ResponseEnum.CancelReceived ||
                result.Response == global::Adyen.Model.Enum.ResponseEnum.CancelOrRefundReceived)
            {
                return true;
            }

            return false;
        }

        private string ReadWebHookContent(HttpRequest httpRequest)
        {
            if (!string.IsNullOrWhiteSpace(webHookContent)) return webHookContent;

            Stream inputStream = httpRequest.GetBufferedInputStream();
            int length = Convert.ToInt32(inputStream.Length);

            byte[] byteArr = new byte[length];
            inputStream.Read(byteArr, 0, length);

            var stringBuilder = new StringBuilder();
            foreach (var b in byteArr)
                stringBuilder.Append(char.ConvertFromUtf32(b));

            webHookContent = stringBuilder.ToString();
            return webHookContent;
        }
    }
}