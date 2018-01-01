﻿namespace GWallet.Backend.Tests

open System

open NUnit.Framework

open GWallet.Backend

module FaultTolerance =

    exception SomeSpecificException

    let private one_consistent_result_because_this_test_doesnt_test_consistency = 1

    let private defaultFaultTolerantClient =
        FaultTolerantClient<SomeSpecificException> one_consistent_result_because_this_test_doesnt_test_consistency

    [<Test>]
    let ``can retrieve basic T for single func``() =
        let someStringArg = "foo"
        let someResult = 1
        let func (arg: string) =
            someResult
        let dataRetreived =
            defaultFaultTolerantClient.Query<string,int> someStringArg [ func ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))

    [<Test>]
    let ``can retrieve basic T for 2 funcs``() =
        let someStringArg = "foo"
        let someResult = 1
        let func1 (arg: string) =
            someResult
        let func2 (arg: string) =
            someResult
        let dataRetreived = defaultFaultTolerantClient.Query<string,int> someStringArg [ func1; func2 ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))

    [<Test>]
    let ``throws ArgumentException if no funcs``(): unit =
        let client = defaultFaultTolerantClient
        Assert.Throws<ArgumentException>(
            fun _ -> client.Query<string,int> "_" [] |> ignore
        ) |> ignore

    [<Test>]
    let ``can retrieve one if 1 of the funcs throws``() =
        let someStringArg = "foo"
        let someResult = 1
        let func1 (arg: string) =
            raise SomeSpecificException
        let func2 (arg: string) =
            someResult
        let dataRetreived =
            defaultFaultTolerantClient.Query<string,int> someStringArg [ func1; func2 ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someResult))

    exception SomeException
    [<Test>]
    let ``NoneAvailable exception contains innerException``() =
        let someStringArg = "foo"
        let func1 (arg: string) =
            raise SomeException
        let func2 (arg: string) =
            raise SomeException

        let dataRetrieved =
            try
                let result =
                    (FaultTolerantClient<SomeException> one_consistent_result_because_this_test_doesnt_test_consistency)
                        .Query<string,int> someStringArg [ func1; func2 ]
                Some(result)
            with
            | ex ->
                Assert.That (ex, Is.TypeOf<NoneAvailableException>())
                Assert.That (ex.InnerException, Is.Not.Null)
                Assert.That (ex.InnerException, Is.TypeOf<SomeException>())
                None

        Assert.That(dataRetrieved, Is.EqualTo(None))

    [<Test>]
    let ``exception typed passed in is ignored``() =
        let someResult = 1
        let someStringArg = "foo"
        let func1 (arg: string) =
            raise SomeException
        let func2 (arg: string) =
            someResult

        let result =
            (FaultTolerantClient<SomeException> one_consistent_result_because_this_test_doesnt_test_consistency)
                .Query<string,int> someStringArg [ func1; func2 ]

        Assert.That(result, Is.EqualTo(someResult))

    exception SomeOtherException
    [<Test>]
    let ``exception not passed in is not ignored``() =
        let someResult = 1
        let someStringArg = "foo"
        let func1 (arg: string) =
            raise SomeOtherException
        let func2 (arg: string) =
            someResult

        Assert.Throws<SomeOtherException>(fun _ ->
            (FaultTolerantClient<SomeException> one_consistent_result_because_this_test_doesnt_test_consistency)
                .Query<string,int> someStringArg [ func1; func2 ]
                    |> ignore ) |> ignore

    [<Test>]
    let ``exception passed in must not be SystemException, otherwise it throws``() =
        let someStringArg = "foo"
        let func1 (arg: string) =
            "someResult1"
        let func2 (arg: string) =
            "someResult2"

        Assert.Throws<ArgumentException>(fun _ ->
            (FaultTolerantClient<Exception> one_consistent_result_because_this_test_doesnt_test_consistency)
                |> ignore ) |> ignore

    [<Test>]
    let ``makes sure data is consistent across N funcs``() =
        let numberOfConsistentResponsesToBeConsideredSafe = 2

        let someStringArg = "foo"
        let someConsistentResult = 1
        let someInconsistentResult = 2

        let funcInconsistent (arg: string) =
            someInconsistentResult
        let funcConsistentA (arg: string) =
            someConsistentResult
        let funcConsistentB (arg: string) =
            someConsistentResult

        let dataRetreived =
            FaultTolerantClient<SomeSpecificException>(numberOfConsistentResponsesToBeConsideredSafe).Query<string,int>
                someStringArg
                [ funcInconsistent; funcConsistentA; funcConsistentB ]
        Assert.That(dataRetreived, Is.TypeOf<int>())
        Assert.That(dataRetreived, Is.EqualTo(someConsistentResult))

    [<Test>]
    let ``consistency precondition > 0``() =
        let numberOfConsistentResponsesToBeConsideredSafe = 0

        Assert.Throws<ArgumentException>(fun _ ->
            FaultTolerantClient<SomeSpecificException>(numberOfConsistentResponsesToBeConsideredSafe)
                    |> ignore ) |> ignore

        let numberOfConsistentResponsesToBeConsideredSafe = -1

        Assert.Throws<ArgumentException>(fun _ ->
            FaultTolerantClient<SomeSpecificException>(numberOfConsistentResponsesToBeConsideredSafe)
                    |> ignore ) |> ignore

    [<Test>]
    let ``consistency precondition > funcs``() =
        let numberOfConsistentResponsesToBeConsideredSafe = 3

        let someStringArg = "foo"
        let someResult = 1

        let func1 (arg: string) =
            someResult
        let func2 (arg: string) =
            someResult

        let client = FaultTolerantClient<SomeSpecificException>(numberOfConsistentResponsesToBeConsideredSafe)

        Assert.Throws<ArgumentException>(fun _ ->
            client.Query<string,int>
                someStringArg
                [ func1; func2 ]
                    |> ignore ) |> ignore

    [<Test>]
    let ``if consistency is not found, throws inconsistency exception``() =
        let numberOfConsistentResponsesToBeConsideredSafe = 3

        let someStringArg = "foo"
        let mostConsistentResult = 1
        let someOtherResultA = 2
        let someOtherResultB = 3

        let func1 (arg: string) =
            someOtherResultA
        let func2 (arg: string) =
            mostConsistentResult
        let func3 (arg: string) =
            someOtherResultB
        let func4 (arg: string) =
            mostConsistentResult

        let client = FaultTolerantClient<SomeSpecificException>(numberOfConsistentResponsesToBeConsideredSafe)

        let inconsistencyEx = Assert.Throws<ResultInconsistencyException>(fun _ ->
                                  client.Query<string,int>
                                      someStringArg
                                      [ func1; func2; func3; func4 ]
                                          |> ignore )
        Assert.That(inconsistencyEx.Message, Is.StringContaining("received: 4, consistent: 2, required: 3"))
