using idempotencia.DTOs;
using idempotencia.Models;

namespace idempotencia.Services;

/// <summary>Cuerpos de correo HTML usados por el flujo de bases de datos.</summary>
public static class EmailTemplates
{
    /// <summary>
    /// Correo enviado la primera vez que un usuario inicia sesión por OAuth y se
    /// le aprovisiona automáticamente su BD MySQL. Es el ÚNICO canal por el que
    /// viaja la contraseña generada en el flujo OAuth: la respuesta de OAuth
    /// vuelve al frontend por redirect/query string, donde no se puede entregar
    /// un secreto de forma segura (ver docs/bugs.md ítem 16). Si el usuario
    /// pierde este correo, puede regenerarla con
    /// <c>POST /databases/{id}/reset-password</c>.
    /// </summary>
    public static string FirstDatabaseCredentials(ProvisionedDatabaseCredentials db) => $"""
        <div style="font-family: sans-serif; max-width: 480px; margin: auto;">
          <h2>Tu primera base de datos en Colmena está lista</h2>
          <p>Al iniciar sesión te creamos automáticamente una base de datos
          <strong>{db.Engine}</strong>. Estos son tus datos de conexión —
          conéctate con tu cliente favorito (MySQL Workbench, DBeaver, etc.):</p>
          <table style="border-collapse: collapse; width: 100%;">
            <tr><td style="padding:4px 8px;"><strong>Motor</strong></td><td style="padding:4px 8px;">{db.Engine}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Servidor (host)</strong></td><td style="padding:4px 8px;">{db.Host}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Puerto</strong></td><td style="padding:4px 8px;">{db.Port}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Base de datos</strong></td><td style="padding:4px 8px;">{db.DbName}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Usuario</strong></td><td style="padding:4px 8px;">{db.LoginName}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Contraseña</strong></td><td style="padding:4px 8px;"><code>{db.Password}</code></td></tr>
          </table>
          <p style="color:#b00; font-size: 0.9em;">
            Guarda esta contraseña en un lugar seguro — no se puede volver a
            mostrar ni recuperar después de este correo (solo se guarda su
            hash). Si la pierdes, puedes generar una nueva desde la app
            (restablecer contraseña de la base de datos).
          </p>
        </div>
        """;

    /// <summary>
    /// Correo enviado tras restablecer la contraseña de una BD aprovisionada
    /// (<c>POST /databases/{id}/reset-password</c>). Es la ÚNICA vez que la
    /// nueva contraseña viaja fuera del backend — no se devuelve en la
    /// respuesta HTTP a propósito (ver docs/API.md).
    /// </summary>
    public static string DatabasePasswordReset(ProvisionedDatabaseDetail db, string newPassword) => $"""
        <div style="font-family: sans-serif; max-width: 480px; margin: auto;">
          <h2>Se restableció la contraseña de tu base de datos</h2>
          <p>La contraseña de acceso a tu base de datos <strong>{db.DbName}</strong>
          ({db.Engine}) fue restablecida. Estos son tus nuevos datos de conexión:</p>
          <table style="border-collapse: collapse; width: 100%;">
            <tr><td style="padding:4px 8px;"><strong>Motor</strong></td><td style="padding:4px 8px;">{db.Engine}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Base de datos</strong></td><td style="padding:4px 8px;">{db.DbName}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Usuario</strong></td><td style="padding:4px 8px;">{db.LoginName}</td></tr>
            <tr><td style="padding:4px 8px;"><strong>Contraseña nueva</strong></td><td style="padding:4px 8px;"><code>{newPassword}</code></td></tr>
          </table>
          <p style="color:#b00; font-size: 0.9em;">
            Guarda esta contraseña en un lugar seguro — no se puede volver a
            mostrar ni recuperar después de este correo (solo se guarda su
            hash). Si no solicitaste este cambio, contacta al soporte de
            Colmena de inmediato.
          </p>
        </div>
        """;
}
